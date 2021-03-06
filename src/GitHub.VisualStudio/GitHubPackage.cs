﻿using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GitHub.Api;
using GitHub.Extensions;
using GitHub.Info;
using GitHub.Logging;
using GitHub.Models;
using GitHub.Services;
using GitHub.ViewModels.GitHubPane;
using GitHub.VisualStudio.Menus;
using GitHub.VisualStudio.UI;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Octokit;
using Serilog;
using Task = System.Threading.Tasks.Task;

namespace GitHub.VisualStudio
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("#110", "#112", System.AssemblyVersionInformation.Version, IconResourceID = 400)]
    [Guid(Guids.guidGitHubPkgString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // Only initialize when we're in the context of a Git repository.
    [ProvideAutoLoad(Guids.UIContext_Git, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideToolWindow(typeof(GitHubPane), Orientation = ToolWindowOrientation.Right, Style = VsDockStyle.Tabbed, Window = EnvDTE.Constants.vsWindowKindSolutionExplorer)]
    [ProvideOptionPage(typeof(OptionsPage), "GitHub for Visual Studio", "General", 0, 0, supportsAutomation: true)]
    public class GitHubPackage : AsyncPackage
    {
        static readonly ILogger log = LogManager.ForContext<GitHubPackage>();

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1823:AvoidUnusedPrivateFields")]
        readonly IServiceProvider serviceProvider;

        public GitHubPackage()
        {
            serviceProvider = this;
        }

        public GitHubPackage(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            LogVersionInformation();
            await base.InitializeAsync(cancellationToken, progress);

            await GetServiceAsync(typeof(IUsageTracker));

            // This package might be loaded on demand so we must await initialization of menus.
            await InitializeMenus();
        }

        void LogVersionInformation()
        {
            var packageVersion = ApplicationInfo.GetPackageVersion(this);
            var hostVersionInfo = ApplicationInfo.GetHostVersionInfo();
            log.Information("Initializing GitHub Extension v{PackageVersion} in {$FileDescription} ({$ProductVersion})",
                packageVersion, hostVersionInfo.FileDescription, hostVersionInfo.ProductVersion);
        }

        async Task InitializeMenus()
        {
            var menus = await GetServiceAsync(typeof(IMenuProvider)) as IMenuProvider;
            if (menus == null)
            {
                // Ignore if null because Expression Blend doesn't support custom services or menu extensibility.
                return;
            }

            foreach (var menu in menus.Menus)
                serviceProvider.AddCommandHandler(menu.Guid, menu.CmdId, (s, e) => menu.Activate());

            foreach (var menu in menus.DynamicMenus)
                serviceProvider.AddCommandHandler(menu.Guid, menu.CmdId, menu.CanShow, () => menu.Activate());
        }

        async Task EnsurePackageLoaded(Guid packageGuid)
        {
            var shell = await GetServiceAsync(typeof(SVsShell)) as IVsShell;
            if (shell != null)
            {
                IVsPackage vsPackage;
                ErrorHandler.ThrowOnFailure(shell.LoadPackage(ref packageGuid, out vsPackage));
            }
        }
    }

    [Export(typeof(IGitHubClient))]
    public class GHClient : GitHubClient
    {
        [ImportingConstructor]
        public GHClient(IProgram program)
            : base(program.ProductHeader)
        {
        }
    }

    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideService(typeof(ILoginManager), IsAsyncQueryable = true)]
    [ProvideService(typeof(IMenuProvider), IsAsyncQueryable = true)]
    [ProvideService(typeof(IGitHubServiceProvider), IsAsyncQueryable = true)]
    [ProvideService(typeof(IUsageTracker), IsAsyncQueryable = true)]
    [ProvideService(typeof(IUsageService), IsAsyncQueryable = true)]
    [ProvideService(typeof(IVSGitExt), IsAsyncQueryable = true)]
    [ProvideService(typeof(IGitHubToolWindowManager))]
    [Guid(ServiceProviderPackageId)]
    public sealed class ServiceProviderPackage : AsyncPackage, IServiceProviderPackage, IGitHubToolWindowManager
    {
        public const string ServiceProviderPackageId = "D5CE1488-DEDE-426D-9E5B-BFCCFBE33E53";
        const string StartPagePreview4PackageId = "3b764d23-faf7-486f-94c7-b3accc44a70d";
        const string StartPagePreview5PackageId = "3b764d23-faf7-486f-94c7-b3accc44a70e";
        static readonly ILogger log = LogManager.ForContext<ServiceProviderPackage>();

        Version vsversion;
        Version VSVersion
        {
            get
            {
                if (vsversion == null)
                {
                    var asm = typeof(ITaskList).Assembly;
                    try
                    {
                        // this will return Microsoft.VisualStudio.Shell.Immutable.14.0 in VS15
                        // but Microsoft.VisualStudio.Shell.Framework in Dev15
                        var vinfo = FileVersionInfo.GetVersionInfo(asm.Location);
                        vsversion = new Version(vinfo.FileMajorPart, vinfo.FileMinorPart, vinfo.FileBuildPart, vinfo.FilePrivatePart);
                    }
                    catch
                    {
                        // something wrong, fallback to assembly version
                        vsversion = asm.GetName().Version;
                    }
                }
                return vsversion;
            }
        }

        protected override Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            AddService(typeof(IGitHubServiceProvider), CreateService, true);
            AddService(typeof(IVSGitExt), CreateService, true);
            AddService(typeof(IUsageTracker), CreateService, true);
            AddService(typeof(IUsageService), CreateService, true);
            AddService(typeof(ILoginManager), CreateService, true);
            AddService(typeof(IMenuProvider), CreateService, true);
            AddService(typeof(IGitHubToolWindowManager), CreateService, true);
            return Task.CompletedTask;
        }

        public async Task<IGitHubPaneViewModel> ShowGitHubPane()
        {
            var pane = ShowToolWindow(new Guid(GitHubPane.GitHubPaneGuid));
            if (pane == null)
                return null;
            var frame = pane.Frame as IVsWindowFrame;
            if (frame != null)
            {
                ErrorHandler.Failed(frame.Show());
            }

            var viewModel = (IGitHubPaneViewModel)((FrameworkElement)pane.Content).DataContext;
            await viewModel.InitializeAsync(pane);
            return viewModel;
        }

        static ToolWindowPane ShowToolWindow(Guid windowGuid)
        {
            IVsWindowFrame frame;
            if (ErrorHandler.Failed(Services.UIShell.FindToolWindow((uint)__VSCREATETOOLWIN.CTW_fForceCreate,
                ref windowGuid, out frame)))
            {
                log.Error("Unable to find or create GitHubPane '{Guid}'", UI.GitHubPane.GitHubPaneGuid);
                return null;
            }
            if (ErrorHandler.Failed(frame.Show()))
            {
                log.Error("Unable to show GitHubPane '{Guid}'", UI.GitHubPane.GitHubPaneGuid);
                return null;
            }

            object docView = null;
            if (ErrorHandler.Failed(frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out docView)))
            {
                log.Error("Unable to grab instance of GitHubPane '{Guid}'", UI.GitHubPane.GitHubPaneGuid);
                return null;
            }
            return docView as GitHubPane;
        }

        async Task<object> CreateService(IAsyncServiceContainer container, CancellationToken cancellationToken, Type serviceType)
        {
            if (serviceType == null)
                return null;

            if (container != this)
                return null;

            if (serviceType == typeof(IGitHubServiceProvider))
            {
                //var sp = await GetServiceAsync(typeof(SVsServiceProvider)) as IServiceProvider;
                var result = new GitHubServiceProvider(this, this);
                await result.Initialize();
                return result;
            }
            else if (serviceType == typeof(ILoginManager))
            {
                // These services are got through MEF and we will take a performance hit if ILoginManager is requested during 
                // InitializeAsync. TODO: We can probably make LoginManager a normal MEF component rather than a service.
                var serviceProvider = await GetServiceAsync(typeof(IGitHubServiceProvider)) as IGitHubServiceProvider;
                var keychain = serviceProvider.GetService<IKeychain>();
                var oauthListener = serviceProvider.GetService<IOAuthCallbackListener>();

                // HACK: We need to make sure this is run on the main thread. We really
                // shouldn't be injecting a view model concern into LoginManager - this
                // needs to be refactored. See #1398.
                var lazy2Fa = new Lazy<ITwoFactorChallengeHandler>(() =>
                    ThreadHelper.JoinableTaskFactory.Run(async () =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        return serviceProvider.GetService<ITwoFactorChallengeHandler>();
                    }));

                return new LoginManager(
                    keychain,
                    lazy2Fa,
                    oauthListener,
                    ApiClientConfiguration.ClientId,
                    ApiClientConfiguration.ClientSecret,
                    ApiClientConfiguration.RequiredScopes,
                    ApiClientConfiguration.AuthorizationNote,
                    ApiClientConfiguration.MachineFingerprint);
            }
            else if (serviceType == typeof(IMenuProvider))
            {
                var sp = await GetServiceAsync(typeof(IGitHubServiceProvider)) as IGitHubServiceProvider;
                return new MenuProvider(sp);
            }
            else if (serviceType == typeof(IUsageService))
            {
                var sp = await GetServiceAsync(typeof(IGitHubServiceProvider)) as IGitHubServiceProvider;
                var environment = new Rothko.Environment();
                return new UsageService(sp, environment);
            }
            else if (serviceType == typeof(IUsageTracker))
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var usageService = await GetServiceAsync(typeof(IUsageService)) as IUsageService;
                var serviceProvider = await GetServiceAsync(typeof(IGitHubServiceProvider)) as IGitHubServiceProvider;
                return new UsageTracker(serviceProvider, usageService);
            }
            else if (serviceType == typeof(IVSGitExt))
            {
                var vsVersion = ApplicationInfo.GetHostVersionInfo().FileMajorPart;
                return VSGitExtFactory.Create(vsVersion, this);
            }
            else if (serviceType == typeof(IGitHubToolWindowManager))
            {
                return this;
            }
            // go the mef route
            else
            {
                var sp = await GetServiceAsync(typeof(IGitHubServiceProvider)) as IGitHubServiceProvider;
                return sp.TryGetService(serviceType);
            }
        }
    }
}
