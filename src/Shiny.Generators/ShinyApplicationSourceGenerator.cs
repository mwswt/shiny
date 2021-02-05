﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;


namespace Shiny.Generators
{
    public abstract class ShinyApplicationSourceGenerator : ISourceGenerator
    {
        const string GENERATED_STARTUP_TYPE_NAME = "AppShinyStartup";
        readonly string osApplicationTypeName;
        List<IAssemblySymbol> shinyAssemblies = null;
        List<INamedTypeSymbol> allSymbols;

        protected ShinyApplicationSourceGenerator(string osApplicationTypeName) => this.osApplicationTypeName = osApplicationTypeName;
        protected GeneratorExecutionContext Context { get; private set; }
        public ShinyApplicationValues ShinyConfig { get; set; }


        public virtual void Execute(GeneratorExecutionContext context)
        {
            context.TryDebug();

            this.Context = context;
            var shinyAppAttributeData = context.GetCurrentAssemblyAttribute(Constants.ShinyApplicationAttributeTypeName);
            if (shinyAppAttributeData == null)
                return;

            var appType = context.Compilation.GetTypeByMetadataName(this.osApplicationTypeName);
            if (appType == null)
                return;

            // allow for testing
            this.ShinyConfig ??= new ShinyApplicationValues(shinyAppAttributeData);

            if (String.IsNullOrWhiteSpace(this.ShinyConfig.ShinyStartupTypeName))
            {
                this.GenerateStartup(this.Context.Compilation.AssemblyName);
                this.ShinyConfig.ShinyStartupTypeName = GENERATED_STARTUP_TYPE_NAME;
            }

            var appClasses = context
                .Compilation
                .Assembly
                .GetAllTypeSymbols()
                .Where(x => x.Inherits(appType))
                .ToList();
            this.Process(appClasses);
        }


        public virtual void Initialize(GeneratorInitializationContext context) { }


        protected abstract void Process(IEnumerable<INamedTypeSymbol> osAppTypeSymbols);


        IndentedStringBuilder builder;
        void GenerateStartup(string nameSpace)
        {
            this.shinyAssemblies = this.Context
                .GetAllAssemblies()
                .Where(x => x
                    .ToDisplayString()
                    .StartsWith("Shiny.")
                )
                .ToList();

            this.allSymbols = this.Context
                .GetAllAssemblies()
                .Where(x => !x.Name.StartsWith("Shiny") && !x.Name.StartsWith("Xamarin."))
                .SelectMany(x => x.GetAllTypeSymbols())
                .Where(x => !x.IsAbstract && x.IsPublic())
                .ToList();

            this.builder = new IndentedStringBuilder();
            this.builder.AppendNamespaces("Microsoft.Extensions.DependencyInjection");

            using (this.builder.BlockInvariant("namespace " + nameSpace))
            {
                using (this.builder.BlockInvariant($"public partial class {GENERATED_STARTUP_TYPE_NAME} : Shiny.IShinyStartup"))
                {
                    this.builder.AppendLine("partial void AdditionalConfigureServices(IServiceCollection services);");

                    using (this.builder.BlockInvariant("public void ConfigureServices(IServiceCollection services)"))
                    {
                        this.builder.AppendLine("this.AdditionalConfigureServices(services);");

                        this.RegisterNoDelegates();
                        this.RegisterWithDelegate();
                        //if (!this.RegisterPush())
                        //    this.RegisterAllDelegate("Shiny.Notifications.INotificationDelegate", "services.UseNotifications", false);

                        if (!this.ShinyConfig.ExcludeJobs)
                            this.RegisterJobs();

                        if (!this.ShinyConfig.ExcludeModules)
                            this.RegisterModules();

                        if (!this.ShinyConfig.ExcludeStartupTasks)
                            this.RegisterStartupTasks();

                        if (!this.ShinyConfig.ExcludeServices)
                            this.RegisterServices();
                    }

                    using (this.builder.BlockInvariant("public void ConfigureApp(IServiceProvider provider)"))
                    {
                        var xamFormsType = this.Context.Compilation.GetTypeByMetadataName("Xamarin.Forms.Internals.DependencyResolver");
                        if (xamFormsType != null)
                        {
                            this.builder.AppendFormatInvariant("global::Xamarin.Forms.Internals.DependencyResolver.ResolveUsing(t => provider.GetService(t));");
                            this.builder.AppendLine();
                        }
                    }
                }
            }
            this.Context.AddSource(GENERATED_STARTUP_TYPE_NAME, this.builder.ToString());
        }


        void RegisterNoDelegates() => this.FindAttributedAssemblies("Shiny.Attributes.AutoStartupAttribute", attributeData =>
        {
            var startupRegistrationServiceExtensionMethodName = attributeData.ConstructorArguments.FirstOrDefault().Value.ToString();
            var registerString = $"services.{startupRegistrationServiceExtensionMethodName}();";
            this.Context.Log(
                "SHINYINFO",
                "Registering in Shiny Startup - " + registerString,
                DiagnosticSeverity.Info
            );
            this.builder.AppendLineInvariant(registerString);
        });


        void RegisterWithDelegate() => this.FindAttributedAssemblies("Shiny.Attributes.AutoStartupWithDelegateAttribute", attributeData =>
        {
            var delegateTypeName = (string)attributeData.ConstructorArguments[0].Value;
            var startupRegistrationServiceExtensionMethodName = (string)attributeData.ConstructorArguments[1].Value;
            var oneDelegateRequiredToInstall = (bool)attributeData.ConstructorArguments[2].Value;

            var symbol = this.Context.Compilation.GetTypeByMetadataName(delegateTypeName);
            var impls = this.allSymbols
                .Where(x => x.Implements(symbol))
                .ToList();

            if (!impls.Any() && oneDelegateRequiredToInstall)
            {
                this.Context.Log(
                    "SHINYDELEGATE",
                    "Required delegate missing for services." + startupRegistrationServiceExtensionMethodName,
                    DiagnosticSeverity.Error
                );
                return;
            }
            var registerStatement = $"services.{startupRegistrationServiceExtensionMethodName}<{impls.First().ToDisplayString()}>();";
            this.builder.AppendLineInvariant(registerStatement);

            if (impls.Count > 1)
            {
                var startIndex = oneDelegateRequiredToInstall ? 1 : 0;
                for (var i = startIndex; i < impls.Count; i++)
                {
                    var impl = impls[i];
                    this.builder.AppendLineInvariant($"services.AddSingleton<{delegateTypeName}, {impl.ToDisplayString()}>();");
                }
            }
        });


        void FindAttributedAssemblies(string attributeName, Action<AttributeData> process)
        {
            foreach (var ass in shinyAssemblies)
            {
                var shinyAttributes = ass
                    .GetAttributes()
                    .Where(x =>
                    {
                        var other = x.AttributeClass.ToDisplayString();
                        return attributeName.Equals(other);
                    })
                    .ToList();

                foreach (var attribute in shinyAttributes)
                    process(attribute);
            }
        }


        void RegisterServices()
        {
            foreach (var symbol in this.allSymbols)
            {
                var attrs = symbol.GetAttributes();
                var hasService = attrs.Any(x => x.AttributeClass.Name.Equals("Shiny.ShinyServiceAttribute"));
                if (hasService)
                {
                    if (!symbol.AllInterfaces.Any())
                    {
                        this.builder.AppendLineInvariant($"services.AddSingleton<{symbol.ToDisplayString()}>();");
                    }
                    else
                    {
                        foreach (var @interface in symbol.AllInterfaces)
                            this.builder.AppendLineInvariant($"services.AddSingleton<{@interface.ToDisplayString()}, {symbol.ToDisplayString()}>();");
                    }
                }
            }
        }


        void RegisterJobs() => this.RegisterTypes(
            "Shiny.Jobs.IJob",
            false,
            x => this.builder.AppendLineInvariant($"services.RegisterJob(typeof({x.ToDisplayString()}));")
        );


        void RegisterStartupTasks() => this.RegisterTypes(
            "Shiny.IShinyStartupTask",
            false,
            x => this.builder.AppendLineInvariant($"services.AddSingleton<{x.ToDisplayString()}>();")
        );


        void RegisterModules() => this.RegisterTypes(
            "Shiny.ShinyModule",
            true,
            x => this.builder.AppendLineInvariant($"services.RegisterModule<{x.ToDisplayString()}>();")
        );


        void RegisterTypes(string searchType, bool inherits, Action<INamedTypeSymbol> action)
        {
            var symbol = this.Context.Compilation.GetTypeByMetadataName(searchType);
            var types = this
                .allSymbols
                .Where(x => inherits
                    ? x.Inherits(symbol)
                    : x.Implements(symbol)
                );

            foreach (var type in types)
                action(type);
        }
    }
}
//static readonly string[] PushCannotGenerateRegister = new []
//{
//    "Shiny.Push.AzureNotificationHubs",
//    "Shiny.Push.Aws"
//};
//static readonly Dictionary<string, string> PushRegisters = new Dictionary<string, string>
//{
//    { "Shiny.Push.FirebaseMessaging", "services.UseFirebaseMessaging" },
//    { "Shiny.Push", "services.UsePush" }
//};

//bool RegisterPush()
//{
//    var registered = false;
//    var cannotRegister = this.Context
//        .Compilation
//        .ReferencedAssemblyNames
//        .FirstOrDefault(x => PushCannotGenerateRegister.Any(y => y.Equals(x.Name)));

//    if (cannotRegister != null)
//    {
//        this.Context.ReportDiagnostic(Diagnostic.Create(
//            new DiagnosticDescriptor(
//                "ShinyPush",
//                $"{cannotRegister.Name} cannot be registered with auto-generation due to required configuration",
//                null,
//                "Push",
//                DiagnosticSeverity.Warning,
//                true
//            ),
//            Location.None
//        ));
//    }
//    else
//    {
//        var register = this.Context.Compilation.ReferencedAssemblyNames.FirstOrDefault(x => PushRegisters.ContainsKey(x.Name));
//        if (register != null)
//        {
//            var registerStatement = PushRegisters[register.Name];
//            this.RegisterAllDelegate("Shiny.Push.IPushDelegate", registerStatement, true);
//            registered = true;
//        }
//    }
//    return registered;
//}
