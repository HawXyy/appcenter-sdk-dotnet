#tool nuget:?package=XamarinComponent
#addin nuget:?package=Cake.FileHelpers
#addin nuget:?package=Cake.Git
#addin nuget:?package=Cake.Incubator
#addin nuget:?package=Cake.Xamarin
#addin "Cake.AzureStorage"
#load "scripts/utility.cake"
#load "scripts/assembly-group.cake"

using System.Net;
using System.Text;
using System.Text.RegularExpressions;

// AppCenter module class definition.
class AppCenterModule {
    public string AndroidModule { get; set; }
    public string DotNetModule { get; set; }
    public string NuGetVersion { get; set; }
    public string PackageId { get; set; }
    public string MainNuspecFilename { get; set; }
    public string NuGetPackageName
    {
        get
        {
            return PackageId + "." + NuGetVersion + ".nupkg";
        }
    }
    public string MacNuspecFilename
    {
        get { return  "Mac" + MainNuspecFilename; }
    }
    public string WindowsNuspecFilename
    {
        get { return  "Windows" + MainNuspecFilename; }
    }
    public AppCenterModule(string android, string dotnet, string mainNuspecFilename) {
        AndroidModule = android;
        DotNetModule = dotnet;
        MainNuspecFilename = mainNuspecFilename;
    }
}

var DownloadedAssembliesFolder = Statics.TemporaryPrefix + "DownloadedAssemblies";
var MacAssembliesZip = Statics.TemporaryPrefix + "MacAssemblies.zip";
var WindowsAssembliesZip = Statics.TemporaryPrefix + "WindowsAssemblies.zip";

// Native SDK versions
var AndroidSdkVersion = "1.0.0";
var IosSdkVersion = "1.0.0";

PlatformPaths AssemblyPlatformPaths;

// URLs for downloading binaries.
/*
 * Read this: http://www.mono-project.com/docs/faq/security/.
 * On Windows,
 *     you have to do additional steps for SSL connection to download files.
 *     http://stackoverflow.com/questions/4926676/mono-webrequest-fails-with-https
 *     By running mozroots and install part of Mozilla's root certificates can make it work.
 */

var SdkStorageUrl = "https://mobilecentersdkdev.blob.core.windows.net/sdk/";
var AndroidUrl = SdkStorageUrl + "AppCenter-SDK-Android-" + AndroidSdkVersion + ".zip";
var IosUrl = SdkStorageUrl + "AppCenter-SDK-Apple-" + IosSdkVersion + ".zip";
var MacAssembliesUrl = SdkStorageUrl + MacAssembliesZip;
var WindowsAssembliesUrl = SdkStorageUrl + WindowsAssembliesZip;

// Available AppCenter modules.
var AppCenterModules = new [] {
    new AppCenterModule("app-center-release.aar", "SDK/AppCenter/Microsoft.AppCenter", "AppCenter.nuspec"),
    new AppCenterModule("app-center-analytics-release.aar", "SDK/AppCenterAnalytics/Microsoft.AppCenter.Analytics", "AppCenterAnalytics.nuspec"),
    new AppCenterModule("app-center-crashes-release.aar", "SDK/AppCenterCrashes/Microsoft.AppCenter.Crashes", "AppCenterCrashes.nuspec"),
    new AppCenterModule("app-center-distribute-release.aar", "SDK/AppCenterDistribute/Microsoft.AppCenter.Distribute", "AppCenterDistribute.nuspec"),
    new AppCenterModule("app-center-push-release.aar", "SDK/AppCenterPush/Microsoft.AppCenter.Push", "AppCenterPush.nuspec"),
    new AppCenterModule("app-center-rum-release.aar", "SDK/AppCenterRum/Microsoft.AppCenter.Rum", "AppCenterRum.nuspec")
};

// Task Target for build
var Target = Argument("Target", Argument("t", "Default"));

// Storage id to append to upload and download file names in storage
var StorageId = Argument("StorageId", Argument("storage-id", ""));

var ConfigFile = "scripts/ac_build_config.xml";

// Prepare the platform paths for downloading, uploading, and preparing assemblies
Setup(context =>
{
    var uploadAssembliesZip = (IsRunningOnUnix() ? MacAssembliesZip : WindowsAssembliesZip) + StorageId;
    var downloadUrl = (IsRunningOnUnix() ? WindowsAssembliesUrl : MacAssembliesUrl) + StorageId;
    var downloadAssembliesZip = (IsRunningOnUnix() ? WindowsAssembliesZip : MacAssembliesZip) + StorageId;
    AssemblyPlatformPaths = new PlatformPaths(uploadAssembliesZip, downloadAssembliesZip, downloadUrl, ConfigFile);
});

// Versioning task.
Task("Version")
    .Does(() =>
{
    var project = ParseProject("./SDK/AppCenter/Microsoft.AppCenter/Microsoft.AppCenter.csproj", configuration: "Release");
    var version = project.NetCore.Version;
    // Extract versions for modules.
    foreach (var module in AppCenterModules)
    {
        module.NuGetVersion = version;
    }
});

// Package id task
Task("PackageId")
    .Does(() =>
{
    // Extract package ids for modules.
    foreach (var module in AppCenterModules)
    {
        var nuspecText = FileReadText("./nuget/" + module.MainNuspecFilename);
        var startTag = "<id>";
        var endTag = "</id>";
        int startIndex = nuspecText.IndexOf(startTag) + startTag.Length;
        int length = nuspecText.IndexOf(endTag) - startIndex;
        var id = nuspecText.Substring(startIndex, length);
        Information("id = " + id);
        module.PackageId = id;
    }
});

Task("Build").IsDependentOn("MacBuild").IsDependentOn("WindowsBuild");

Task("MacBuild")
    .WithCriteria(() => IsRunningOnUnix())
    .Does(() =>
{
    // Run externals here instead of using dependency so that this doesn't get called on windows
    RunTarget("Externals");
    // Build solution
    NuGetRestore("./AppCenter-SDK-Build-Mac.sln");
    MSBuild("./AppCenter-SDK-Build-Mac.sln", c => c.Configuration = "Release");
}).OnError(HandleError);

// Building Windows code task
Task("WindowsBuild")
    .WithCriteria(() => !IsRunningOnUnix())
    .Does(() =>
{
    // Build solution
    NuGetRestore("./AppCenter-SDK-Build-Windows.sln");
    MSBuild("./AppCenter-SDK-Build-Windows.sln", settings => settings.SetConfiguration("Release").WithProperty("Platform", "x86"));
    MSBuild("./AppCenter-SDK-Build-Windows.sln", settings => settings.SetConfiguration("Release").WithProperty("Platform", "x64"));
    MSBuild("./AppCenter-SDK-Build-Windows.sln", settings => settings.SetConfiguration("Release").WithProperty("Platform", "ARM"));
    MSBuild("./AppCenter-SDK-Build-Windows.sln", settings => settings.SetConfiguration("Release")); // any cpu
    MSBuild("./AppCenter-SDK-Build-Windows.sln", settings => settings.SetConfiguration("Reference")); // any cpu
}).OnError(HandleError);

Task("PrepareAssemblies").IsDependentOn("Build").Does(()=>
{
    foreach (var assemblyGroup in AssemblyPlatformPaths.UploadAssemblyGroups)
    {
        CopyFiles(assemblyGroup.AssemblyPaths, assemblyGroup.Folder);
    }
}).OnError(HandleError);

// Task dependencies for binding each platform.
Task("Bindings-Android").IsDependentOn("Externals-Android");
Task("Bindings-Ios").IsDependentOn("Externals-Ios");

// Downloading Android binaries.
Task("Externals-Android")
    .Does(() =>
{
    CleanDirectory("./externals/android");

    // Download zip file.
    DownloadFile(AndroidUrl, "./externals/android/android.zip");
    Unzip("./externals/android/android.zip", "./externals/android/");

    // Move binaries to externals/android so that linked files don't have versions
    // in their paths
    var files = GetFiles("./externals/android/*/*");
    CopyFiles(files, "./externals/android/");
}).OnError(HandleError);

// Downloading iOS binaries.
Task("Externals-Ios")
    .Does(() =>
{
    CleanDirectory("./externals/ios");

    // Download zip file containing AppCenter frameworks
    DownloadFile(IosUrl, "./externals/ios/ios.zip");
    Unzip("./externals/ios/ios.zip", "./externals/ios/");

    // Copy the AppCenter binaries directly from the frameworks and add the ".a" extension
    var files = GetFiles("./externals/ios/*/iOS/*.framework/AppCenter*");
    foreach (var file in files)
    {
        MoveFile(file, "./externals/ios/" + file.GetFilename() + ".a");
    }

    // Copy Distribute resource bundle and copy it to the externals directory.
    if(DirectoryExists("./externals/ios/AppCenter-SDK-Apple/iOS/AppCenterDistributeResources.bundle"))
    {
        MoveDirectory("./externals/ios/AppCenter-SDK-Apple/iOS/AppCenterDistributeResources.bundle", "./externals/ios/AppCenterDistributeResources.bundle");
    }
}).OnError(HandleError);

// Create a common externals task depending on platform specific ones
Task("Externals").IsDependentOn("Externals-Ios").IsDependentOn("Externals-Android");

// Main Task.
Task("Default").IsDependentOn("NuGet").IsDependentOn("RemoveTemporaries");

// Build tests
Task("UITest").IsDependentOn("RestoreTestPackages").Does(() =>
{
    MSBuild("./Tests/UITests/Contoso.Forms.Test.UITests.csproj", c => c.Configuration = "Release");
});

// Pack NuGets for appropriate platform
Task("NuGet")
    .IsDependentOn("PrepareAssemblies")
    .IsDependentOn("Version")
    .Does(()=>
{
    CleanDirectory("output");
    var specCopyName = Statics.TemporaryPrefix + "spec_copy.nuspec";

    // Package NuGets.
    foreach (var module in AppCenterModules)
    {
        var nuspecFilename = "nuget/" + (IsRunningOnUnix() ? module.MacNuspecFilename : module.WindowsNuspecFilename);

        // Skip modules that don't have nuspecs.
        if (!FileExists(nuspecFilename))
        {
            continue;
        }

        // Prepare nuspec by making substitutions in a copied nuspec (to avoid altering the original)
        CopyFile(nuspecFilename, specCopyName);
        ReplaceAssemblyPathsInNuspecs(specCopyName);
        Information("Building a NuGet package for " + module.DotNetModule + " version " + module.NuGetVersion);
        NuGetPack(File(specCopyName), new NuGetPackSettings {
            Verbosity = NuGetVerbosity.Detailed,
            Version = module.NuGetVersion,
            RequireLicenseAcceptance = true
        });

        // Clean up
        DeleteFiles(specCopyName);
    }
    MoveFiles("Microsoft.AppCenter*.nupkg", "output");
}).OnError(HandleError);

// Add version to nuspecs for vsts (the release definition does not have the solutions and thus cannot extract a version from them)
Task("PrepareNuspecsForVSTS").IsDependentOn("Version").Does(()=>
{
    foreach (var module in AppCenterModules)
    {
        ReplaceTextInFiles("./nuget/" + module.MainNuspecFilename, "$version$", module.NuGetVersion);
    }
});

// Upload assemblies to Azure storage
Task("UploadAssemblies")
    .IsDependentOn("PrepareAssemblies")
    .Does(()=>
{
    // The environment variables below must be set for this task to succeed
    var apiKey = EnvironmentVariable("AZURE_STORAGE_ACCESS_KEY");
    var accountName = EnvironmentVariable("AZURE_STORAGE_ACCOUNT");

    foreach (var assemblyGroup in AssemblyPlatformPaths.UploadAssemblyGroups)
    {
        var destinationFolder =  DownloadedAssembliesFolder + "/" + assemblyGroup.Folder;
        CleanDirectory(destinationFolder);
        CopyFiles(assemblyGroup.AssemblyPaths, destinationFolder);
    }

    Information("Uploading to blob " + AssemblyPlatformPaths.UploadAssembliesZip);
    Zip(DownloadedAssembliesFolder, AssemblyPlatformPaths.UploadAssembliesZip);
    AzureStorage.UploadFileToBlob(new AzureStorageSettings
    {
        AccountName = accountName,
        ContainerName = "sdk",
        BlobName = AssemblyPlatformPaths.UploadAssembliesZip,
        Key = apiKey,
        UseHttps = true
    }, AssemblyPlatformPaths.UploadAssembliesZip);

}).OnError(HandleError).Finally(()=>RunTarget("RemoveTemporaries"));

// Download assemblies from azure storage
Task("DownloadAssemblies").Does(()=>
{
    Information("Fetching assemblies from url: " + AssemblyPlatformPaths.DownloadUrl);
    CleanDirectory(DownloadedAssembliesFolder);
    DownloadFile(AssemblyPlatformPaths.DownloadUrl, AssemblyPlatformPaths.DownloadAssembliesZip);
    Unzip(AssemblyPlatformPaths.DownloadAssembliesZip, DownloadedAssembliesFolder);
    DeleteFiles(AssemblyPlatformPaths.DownloadAssembliesZip);
    Information("Successfully downloaded assemblies.");
}).OnError(HandleError);

Task("MergeAssemblies")
    .IsDependentOn("PrepareAssemblies")
    .IsDependentOn("DownloadAssemblies")
    .IsDependentOn("Version")
    .Does(()=>
{
    Information("Beginning complete package creation...");

    // Copy the downloaded files to their proper locations so the structure is as if
    // the downloaded assemblies were built locally (for the nuspecs to work)
    foreach (var downloadGroup in AssemblyPlatformPaths.DownloadAssemblyGroups)
    {
        var assemblyFolder = downloadGroup.Folder;
        CleanDirectory(assemblyFolder);
        var files = GetFiles(DownloadedAssembliesFolder + "/" + assemblyFolder + "/*");
        CopyFiles(files, assemblyFolder);
    }

    // Create NuGet packages
    foreach (var module in AppCenterModules)
    {
        var specCopyName = Statics.TemporaryPrefix + "spec_copy.nuspec";

        // Prepare nuspec by making substitutions in a copied nuspec (to avoid altering the original)
        CopyFile("nuget/" + module.MainNuspecFilename, specCopyName);
        ReplaceAssemblyPathsInNuspecs(specCopyName);

        // Create the NuGet package
        Information("Building a NuGet package for " + module.DotNetModule + " version " + module.NuGetVersion);
        NuGetPack(File(specCopyName), new NuGetPackSettings {
            Verbosity = NuGetVerbosity.Detailed,
            Version = module.NuGetVersion,
            RequireLicenseAcceptance = true
        });

        // Clean up
        DeleteFiles(specCopyName);
    }
    CleanDirectory("output");
    MoveFiles("*.nupkg", "output");
}).OnError(HandleError).Finally(()=>RunTarget("RemoveTemporaries"));

Task("TestApps").IsDependentOn("UITest").Does(() =>
{
    // Build and package the test applications
    MSBuild("./Tests/iOS/Contoso.Forms.Test.iOS.csproj", settings => settings.SetConfiguration("Release")
      .WithTarget("Build")
      .WithProperty("Platform", "iPhone")
      .WithProperty("BuildIpa", "true")
      .WithProperty("OutputPath", "bin/iPhone/Release/")
      .WithProperty("AllowUnsafeBlocks", "true"));
    AndroidPackage("./Tests/Droid/Contoso.Forms.Test.Droid.csproj", false, c => c.Configuration = "Release");
}).OnError(HandleError);

Task("RestoreTestPackages").Does(() =>
{
    NuGetRestore("./AppCenter-SDK-Test.sln");
    NuGetUpdate("./Tests/Contoso.Forms.Test/packages.config");
    NuGetUpdate("./Tests/iOS/packages.config");
    NuGetUpdate("./Tests/Droid/packages.config", new NuGetUpdateSettings {

        // workaround for https://stackoverflow.com/questions/44861995/xamarin-build-error-building-Target
        Source = new string[] { EnvironmentVariable("NUGET_URL") }
    });
}).OnError(HandleError);

// Remove any uploaded nugets from azure storage
Task("CleanAzureStorage").Does(()=>
{
    var apiKey = EnvironmentVariable("AZURE_STORAGE_ACCESS_KEY");
    var accountName = EnvironmentVariable("AZURE_STORAGE_ACCOUNT");

    try
    {
        AzureStorage.DeleteBlob(new AzureStorageSettings
        {
            AccountName = accountName,
            ContainerName = "sdk",
            BlobName = MacAssembliesZip + StorageId,
            Key = apiKey,
            UseHttps = true
        });
        AzureStorage.DeleteBlob(new AzureStorageSettings
        {
            AccountName = accountName,
            ContainerName = "sdk",
            BlobName = WindowsAssembliesZip + StorageId,
            Key = apiKey,
            UseHttps = true
        });
    }
    catch
    {
        // not an error if the blob is not found
    }
}).OnError(HandleError);

Task("PrepareAssemblyPathsVSTS").Does(()=>
{
    var nuspecPathPrefix = EnvironmentVariable("NUSPEC_PATH");
    foreach (var module in AppCenterModules)
    {
        var nuspecPath = System.IO.Path.Combine(nuspecPathPrefix, module.MainNuspecFilename);
        ReplaceAssemblyPathsInNuspecs(nuspecPath);
    }
}).OnError(HandleError);

Task("NugetPackVSTS").Does(()=>
{
    var nuspecPathPrefix = EnvironmentVariable("NUSPEC_PATH");
    foreach (var module in AppCenterModules)
    {
        var spec = GetFiles(nuspecPathPrefix + module.MainNuspecFilename);

        // Create the NuGet packages.
        Information("Building a NuGet package for " + module.MainNuspecFilename);
        NuGetPack(spec, new NuGetPackSettings {
            Verbosity = NuGetVerbosity.Detailed,
            RequireLicenseAcceptance = true
        });
    }
}).OnError(HandleError);

void ReplaceAssemblyPathsInNuspecs(string nuspecPath)
{
    // For the Tuples, Item1 is variable name, Item2 is variable value.
    var assemblyPathVars = new List<Tuple<string, string>>();
    foreach (var group in AssemblyPlatformPaths.UploadAssemblyGroups)
    {
        if (group.NuspecKey == null)
        {
            continue;
        }
        var environmentVariableName = group.Id.ToUpper() + "_ASSEMBLY_PATH_NUSPEC";
        var tuple = Tuple.Create(group.NuspecKey,
                    EnvironmentVariable(environmentVariableName, group.Folder));
        assemblyPathVars.Add(tuple);
    }
    foreach (var assemblyPathVar in assemblyPathVars)
    {
        ReplaceTextInFiles(nuspecPath, assemblyPathVar.Item1, assemblyPathVar.Item2);
    }
}

RunTarget(Target);
