﻿using NuGet.Client;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.PackagingCore;
using NuGet.ProjectManagement;
using NuGet.Resolver;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NuGet.PackageManagement
{
    /// <summary>
    /// NuGetPackageManager orchestrates a nuget package operation such as an install or uninstall
    /// It is to be called by various NuGet Clients including the custom third-party ones
    /// </summary>
    public class NuGetPackageManager
    {
        /// <summary>
        /// Event to be raised while installing a package
        /// </summary>
        public event EventHandler<PackageOperationEventArgs> PackageInstalling;
        /// <summary>
        /// Event to be raised while installing a package
        /// </summary>
        public event EventHandler<PackageOperationEventArgs> PackageInstalled;
        /// <summary>
        /// Event to be raised while installing a package
        /// </summary>
        public event EventHandler<PackageOperationEventArgs> PackageUninstalling;
        /// <summary>
        /// Event to be raised while installing a package
        /// </summary>
        public event EventHandler<PackageOperationEventArgs> PackageUninstalled;

        private SourceRepositoryProvider SourceRepositoryProvider { get; set; }

        /// <summary>
        /// Creates a NuGetPackageManager for a given <param name="sourceRepositoryProvider"></param>
        /// </summary>
        public NuGetPackageManager(SourceRepositoryProvider sourceRepositoryProvider/*, IPackageResolver packageResolver */)
        {
            if (sourceRepositoryProvider == null)
            {
                throw new ArgumentNullException("sourceRepositoryProvider");
            }

            SourceRepositoryProvider = sourceRepositoryProvider;
        }

        /// <summary>
        /// Installs the latest version of the given <param name="packageId"></param> to NuGetProject <param name="nuGetProject"></param>
        /// <param name="resolutionContext"></param> and <param name="nuGetProjectContext"></param> are used in the process
        /// </summary>
        public async Task InstallPackageAsync(NuGetProject nuGetProject, string packageId, ResolutionContext resolutionContext, INuGetProjectContext nuGetProjectContext)
        {
            // Step-1 : Get latest version for packageId
            var latestVersion = await GetLatestVersionAsync(packageId, resolutionContext);

            if(latestVersion == null)
            {
                throw new InvalidOperationException(Strings.NoLatestVersionFound);
            }

            // Step-2 : Call InstallPackageAsync(project, packageIdentity)
            await InstallPackageAsync(nuGetProject, new PackageIdentity(packageId, latestVersion), resolutionContext, nuGetProjectContext);
        }

        /// <summary>
        /// Installs given <param name="packageIdentity"></param> to NuGetProject <param name="nuGetProject"></param>
        /// <param name="resolutionContext"></param> and <param name="nuGetProjectContext"></param> are used in the process
        /// </summary>
        public async Task InstallPackageAsync(NuGetProject nuGetProject, PackageIdentity packageIdentity, ResolutionContext resolutionContext, INuGetProjectContext nuGetProjectContext)
        {
            // Step-1 : Call PreviewInstallPackagesAsync to get all the nuGetProjectActions
            var nuGetProjectActions = await PreviewInstallPackageAsync(nuGetProject, packageIdentity, resolutionContext, nuGetProjectContext);

            // Step-2 : Execute all the nuGetProjectActions
            await ExecuteNuGetProjectActionsAsync(nuGetProject, nuGetProjectActions, nuGetProjectContext);
        }

        /// <summary>
        /// Gives the preview as a list of NuGetProjectActions that will be performed to install <param name="packageId"></param> into <param name="nuGetProject"></param>
        /// <param name="resolutionContext"></param> and <param name="nuGetProjectContext"></param> are used in the process
        /// </summary>
        public async Task<IEnumerable<NuGetProjectAction>> PreviewInstallPackageAsync(NuGetProject nuGetProject, string packageId, ResolutionContext resolutionContext, INuGetProjectContext nuGetProjectContext)
        {
            // Step-1 : Get latest version for packageId
            var latestVersion = await GetLatestVersionAsync(packageId, resolutionContext);

            if (latestVersion == null)
            {
                throw new InvalidOperationException(Strings.NoLatestVersionFound);
            }

            // Step-2 : Call InstallPackage(project, packageIdentity)
            return await PreviewInstallPackageAsync(nuGetProject, new PackageIdentity(packageId, latestVersion), resolutionContext, nuGetProjectContext);
        }

        /// <summary>
        /// Gives the preview as a list of NuGetProjectActions that will be performed to install <param name="packageIdentity"></param> into <param name="nuGetProject"></param>
        /// <param name="resolutionContext"></param> and <param name="nuGetProjectContext"></param> are used in the process
        /// </summary>
        public async Task<IEnumerable<NuGetProjectAction>> PreviewInstallPackageAsync(NuGetProject nuGetProject, PackageIdentity packageIdentity, ResolutionContext resolutionContext, INuGetProjectContext nuGetProjectContext)
        {
            var packagesToInstall = new List<PackageIdentity>() { packageIdentity };
            // Step-1 : Get metadata resources using gatherer
            var targetFramework = nuGetProject.GetMetadata<NuGetFramework>(NuGetProjectMetadataKeys.TargetFramework);
            nuGetProjectContext.Log(MessageLevel.Info, Strings.AttemptingToGatherDependencyInfo, packageIdentity, targetFramework);
            var availablePackageDependencyInfoWithSourceSet = await GatherPackageDependencyInfo(packageIdentity, targetFramework);
            if(availablePackageDependencyInfoWithSourceSet.Count == 0)
            {
                throw new InvalidOperationException(String.Format(Strings.UnableToGatherDependencyInfo, packageIdentity));
            }

            // Step-2 : Call IPackageResolver.Resolve to get new list of installed packages
            var projectInstalledPackageReferences = nuGetProject.GetInstalledPackages();
            // TODO: Consider using IPackageResolver once it is extensible
            var packageResolver = new PackageResolver(resolutionContext.DependencyBehavior);
            nuGetProjectContext.Log(MessageLevel.Info, Strings.AttemptingToResolveDependencies, packageIdentity, resolutionContext.DependencyBehavior);
            IEnumerable<PackageIdentity> newListOfInstalledPackages = packageResolver.Resolve(packagesToInstall, availablePackageDependencyInfoWithSourceSet.Keys, projectInstalledPackageReferences);
            if(newListOfInstalledPackages == null)
            {
                throw new InvalidOperationException(String.Format(Strings.UnableToResolveDependencyInfo, packageIdentity, resolutionContext.DependencyBehavior));
            }

            // Step-3 : Get the list of nuGetProjectActions to perform, install/uninstall on the nugetproject
            // based on newPackages obtained in Step-2 and project.GetInstalledPackages
            var oldListOfInstalledPackages = projectInstalledPackageReferences.Select(p => p.PackageIdentity);

            nuGetProjectContext.Log(MessageLevel.Info, Strings.ResolvingActionsToInstallPackage, packageIdentity);
            var newPackagesToUninstall = oldListOfInstalledPackages
                .Where(op => newListOfInstalledPackages
                    .Where(np => op.Id.Equals(np.Id, StringComparison.OrdinalIgnoreCase)).Any());
            var newPackagesToInstall = newListOfInstalledPackages.Where(p => !oldListOfInstalledPackages.Contains(p));

            List<NuGetProjectAction> nuGetProjectActions = new List<NuGetProjectAction>();
            foreach (PackageIdentity newPackageToUninstall in newPackagesToUninstall)
            {
                nuGetProjectActions.Add(NuGetProjectAction.CreateUninstallProjectAction(newPackageToUninstall));
            }

            foreach (PackageIdentity newPackageToInstall in newPackagesToInstall)
            {
                SourceRepository sourceRepository;
                var fakePackageDependencyInfo = new PackageDependencyInfo(newPackageToInstall, null);
                if (!availablePackageDependencyInfoWithSourceSet.TryGetValue(fakePackageDependencyInfo, out sourceRepository))
                {
                    throw new InvalidOperationException();
                }

                nuGetProjectActions.Add(NuGetProjectAction.CreateInstallProjectAction(newPackageToInstall, sourceRepository));
            }

            nuGetProjectContext.Log(MessageLevel.Info, Strings.ResolvedActionsToInstallPackage, packageIdentity);
            return nuGetProjectActions;
        }

        /// <summary>
        /// Executes the list of <param name="nuGetProjectActions"></param> on <param name="nuGetProject"></param>, which is likely obtained by calling into PreviewInstallPackageAsync
        /// <param name="nuGetProjectContext"></param> is used in the process
        /// </summary>
        public async Task ExecuteNuGetProjectActionsAsync(NuGetProject nuGetProject, IEnumerable<NuGetProjectAction> nuGetProjectActions, INuGetProjectContext nuGetProjectContext)
        {
            foreach (NuGetProjectAction nuGetProjectAction in nuGetProjectActions)
            {
                if (nuGetProjectAction.NuGetProjectActionType == NuGetProjectActionType.Uninstall)
                {
                    ExecuteUninstall(nuGetProject, nuGetProjectAction.PackageIdentity, nuGetProjectContext);
                }
                else
                {
                    using (var targetPackageStream = new MemoryStream())
                    {
                        await PackageDownloader.GetPackageStream(nuGetProjectAction.SourceRepository, nuGetProjectAction.PackageIdentity, targetPackageStream);
                        ExecuteInstall(nuGetProject, nuGetProjectAction.PackageIdentity, targetPackageStream, nuGetProjectContext);
                    }
                }
            }
        }

        private void ExecuteInstall(NuGetProject nuGetProject, PackageIdentity packageIdentity, Stream packageStream, INuGetProjectContext nuGetProjectContext)
        {
            var packageOperationEventArgs = new PackageOperationEventArgs(packageIdentity);
            if(PackageInstalling != null)
            {
                PackageInstalling(this, packageOperationEventArgs);
            }
            nuGetProject.InstallPackage(packageIdentity, packageStream, nuGetProjectContext);

            // TODO: Consider using CancelEventArgs instead of a regular EventArgs??
            //if (packageOperationEventArgs.Cancel)
            //{
            //    return;
            //}

            if(PackageInstalled != null)
            {
                PackageInstalled(this, packageOperationEventArgs);
            }
        }

        private void ExecuteUninstall(NuGetProject nuGetProject, PackageIdentity packageIdentity, INuGetProjectContext nuGetProjectContext)
        {
            var packageOperationEventArgs = new PackageOperationEventArgs(packageIdentity);
            if (PackageUninstalling != null)
            {
                PackageUninstalling(this, packageOperationEventArgs);
            }
            nuGetProject.UninstallPackage(packageIdentity, nuGetProjectContext);

            // TODO: Consider using CancelEventArgs instead of a regular EventArgs??
            //if (packageOperationEventArgs.Cancel)
            //{
            //    return;
            //}

            if (PackageUninstalled != null)
            {
                PackageUninstalled(this, packageOperationEventArgs);
            }
        }

        private async Task<NuGetVersion> GetLatestVersionAsync(string packageId, ResolutionContext resolutionContext)
        {
            List<NuGetVersion> latestVersionFromDifferentRepositories = new List<NuGetVersion>();
            foreach (var sourceRepository in SourceRepositoryProvider.GetRepositories())
            {
                var metadataResource = sourceRepository.GetResource<MetadataResource>();
                if (metadataResource != null)
                {
                    var latestVersionKeyPairList = await metadataResource.GetLatestVersions(new List<string>() { packageId },
                        resolutionContext.IncludePrerelease, resolutionContext.IncludeUnlisted, CancellationToken.None);
                    if((latestVersionKeyPairList == null || !latestVersionKeyPairList.Any()))
                    {
                        continue;
                    }
                    latestVersionFromDifferentRepositories.Add(latestVersionKeyPairList.FirstOrDefault().Value);
                }
            }

            return latestVersionFromDifferentRepositories.Count == 0 ? null : latestVersionFromDifferentRepositories.Max<NuGetVersion>();
        }

        private async Task<IDictionary<PackageDependencyInfo, SourceRepository>> GatherPackageDependencyInfo(PackageIdentity packageIdentity, NuGetFramework targetFramework)
        {
            // get a distinct set of packages from all repos
            var packageDependencyInfoSet = new Dictionary<PackageDependencyInfo, SourceRepository>(PackageDependencyInfo.Comparer);

            // find all needed packages from online
            foreach (var sourceRepository in SourceRepositoryProvider.GetRepositories())
            {
                // get the resolver data resource
                var dependencyInfoResource = sourceRepository.GetResource<DepedencyInfoResource>();

                // resources can always be null
                if (dependencyInfoResource != null)
                {
                    var packageDependencyInfo = await dependencyInfoResource.ResolvePackages(new PackageIdentity[] { packageIdentity }, targetFramework, true);

                    foreach (var pkgDepInfo in packageDependencyInfo)
                    {
                        if(!packageDependencyInfoSet.ContainsKey(pkgDepInfo))
                        {
                            packageDependencyInfoSet.Add(pkgDepInfo, sourceRepository);
                        }
                    }
                }
            }

            return packageDependencyInfoSet;
        }
    }

    /// <summary>
    /// The event args class used in raising package operation events
    /// </summary>
    public  class PackageOperationEventArgs : EventArgs
    {
        PackageIdentity PackageIdentity { get; set; }
        /// <summary>
        /// Creates a package operation event args object for given <param name="packageIdentity"></param>
        /// </summary>
        public PackageOperationEventArgs(PackageIdentity packageIdentity)
        {
            PackageIdentity = packageIdentity;
        }
    }
}
