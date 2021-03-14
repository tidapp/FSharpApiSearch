// --------------------------------------------------------------------------------------
// FAKE build script
// --------------------------------------------------------------------------------------
#r "paket: groupref Build //"

// #r "nuget: Fake.Core.Targets, 5.0.0-alpha018"
// #r "nuget: Fake.Tools.Git, 5.20.4-alpha.1642"
// #r "nuget: Fake.DotNet.Cli, 5.20.4-alpha.1642"
// #r "nuget: Fake.Core.ReleaseNotes, 5.20.4-alpha.1642"
// #r "nuget: Persimmon.Console, 4.0.2"
// #r "nuget: Fake.API.GitHub, 5.20.4-alpha.1642"
// #r "nuget: Fake.IO.Zip, 5.20.4-alpha.1642"
// #r "nuget: Fake.Core.UserInput, 5.20.4-alpha.1642"

open Fake.Core
open Fake.Tools
open Fake.IO
open Fake.DotNet
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators
open System
open System.IO
open Fake.Api

// --------------------------------------------------------------------------------------
// START TODO: Provide project-specific details below
// --------------------------------------------------------------------------------------

// Information about the project are used
//  - for version and project name in generated AssemblyInfo file
//  - by the generated NuGet package
//  - to run tests and to publish documentation on GitHub gh-pages
//  - for documentation, you also need to edit info in "docs/tools/generate.fsx"

// The name of the project
// (used by attributes in AssemblyInfo, name of a NuGet package and directory in 'src')
let project = "FSharpApiSearch"

// Short summary of the project
// (used as description in AssemblyInfo and as a short summary for NuGet package)
let summary = "F# API search engine"

// Longer description of the project
// (used as a description for NuGet package; line breaks are automatically cleaned up)
let description = "F# API search engine"

// List of author names (for NuGet package)
let authors = [ "hafuu" ]

// Tags for your project (for NuGet package)
let tags = ""

// File system information
let solutionFile  = "FSharpApiSearch.sln"

// Default target configuration
let configuration = DotNet.BuildConfiguration.fromString "Release"

// Pattern specifying assemblies to be tested using NUnit
let testAssemblies = "tests/**/bin" </> string configuration </> "*Tests*.dll"

// Git configuration (used for publishing documentation in gh-pages branch)
// The profile where the project is posted
let gitOwner = "hafuu"
let gitHome = sprintf "%s/%s" "https://github.com" gitOwner

// The name of the project on GitHub
let gitName = "FSharpApiSearch"

// The url for the raw files hosted
let gitRaw = Environment.environVarOrDefault "gitRaw" "https://raw.githubusercontent.com/hafuu"

// --------------------------------------------------------------------------------------
// END TODO: The rest of the file includes standard build steps
// --------------------------------------------------------------------------------------

// Read additional information from the release notes document
let release = ReleaseNotes.load "RELEASE_NOTES.md"

// Helper active pattern for project types
let (|Fsproj|Csproj|Vbproj|Shproj|) (projFileName:string) =
    match projFileName with
    | f when f.EndsWith("fsproj") -> Fsproj
    | f when f.EndsWith("csproj") -> Csproj
    | f when f.EndsWith("vbproj") -> Vbproj
    | f when f.EndsWith("shproj") -> Shproj
    | _                           -> failwith (sprintf "Project file %s not supported. Unknown project type." projFileName)

// Copies binaries from default VS location to expected bin folder
// But keeps a subdirectory structure for each project in the
// src folder to support multiple project outputs
Target.Create "CopyBinaries" (fun _ ->
    !! "src/**/*.??proj"
    -- "src/**/*.shproj"
    |>  Seq.map (fun f -> ((System.IO.Path.GetDirectoryName f) </> "bin" </> string configuration, "bin" </> (System.IO.Path.GetFileNameWithoutExtension f)))
    |>  Seq.iter (fun (fromDir, toDir) -> Shell.copyDir toDir fromDir (fun _ -> true))
)

// --------------------------------------------------------------------------------------
// Clean build results

let vsProjProps = 
    [ ("Configuration", string configuration) ]

Target.Create "Clean" (fun _ ->
    let cleanArgs (o: DotNet.MSBuildOptions) = 
        { o with
            MSBuildParams = { o.MSBuildParams with 
                                Targets = ["Clean"]
                                Properties = vsProjProps } }
    !! solutionFile
    |> Seq.head
    |> DotNet.msbuild cleanArgs
    Shell.cleanDirs ["bin"; "temp"; "docs/output"]
)

// --------------------------------------------------------------------------------------
// Build library & test project

Target.Create "Build" (fun _ ->
    let buildArgs (o: DotNet.BuildOptions) = 
        { o with 
            Configuration = configuration }
    !! solutionFile
    |> Seq.head
    |> DotNet.build buildArgs
    |> ignore
)

// --------------------------------------------------------------------------------------
// Run the unit tests using test runner

// Target.Create "RunTests" (fun _ ->
//     !! testAssemblies
//     |> Persimmon id
// )

// --------------------------------------------------------------------------------------
// Build a NuGet package

Target.Create "NuGet" (fun _ ->
    DotNet.pack 
        (fun (p: DotNet.PackOptions) ->
            { p with
                Configuration = configuration
                OutputPath = Some "bin"
                MSBuildParams = { p.MSBuildParams with 
                                    Properties = [
                                        "Version", release.NugetVersion
                                        "ReleaseNotes", release.Notes |> String.concat "\n"
                                    ]} }
                ) solutionFile
)

Target.Create "PublishNuget" (fun _ ->
    !! "bin/**.nupkg"
    |> Seq.iter (fun pkg ->
        let args (p: DotNet.NuGetPushOptions) = 
            p
        DotNet.nugetPush args pkg
    )
)

// --------------------------------------------------------------------------------------
// Release Scripts

open Octokit

Target.Create "Release" (fun _ ->
    let user =
        match Environment.environVarOrNone "github-user" with
        | Some s when not (String.IsNullOrWhiteSpace s) -> s
        | _ -> UserInput.getUserInput "Username: "
    let pw =
        match Environment.environVarOrNone "github-pw" with
        | Some s when not (String.IsNullOrWhiteSpace s) -> s
        | _ -> UserInput.getUserPassword "Password: "
    let remote =
        Git.CommandHelper.getGitResult "" "remote -v"
        |> Seq.filter (fun (s: string) -> s.EndsWith("(push)"))
        |> Seq.tryFind (fun (s: string) -> s.Contains(gitOwner + "/" + gitName))
        |> function None -> gitHome + "/" + gitName | Some (s: string) -> s.Split().[0]

    Git.Staging.stageAll ""
    Git.Commit.exec "" (sprintf "Bump version to %s" release.NugetVersion)
    Git.Branches.pushBranch "" remote (Git.Information.getBranchName "")

    Git.Branches.tag "" release.NugetVersion
    Git.Branches.pushTag "" remote release.NugetVersion

    let setReleaseParams (p: GitHub.CreateReleaseParams) = 
        { p with 
            Draft = release.SemVer.PreRelease <> None
            Body = release.Notes |> String.concat "\n" }
    // release on github
    GitHub.createClient user pw
    |> GitHub.createRelease gitOwner gitName release.NugetVersion setReleaseParams
    |> Async.RunSynchronously
    |> ignore
)

Target.Create "BuildPackage" Target.DoNothing

// --------------------------------------------------------------------------------------
// Run all targets by default. Invoke 'build <Target>' to override

Target.Create "All" Target.DoNothing

open Fake.Core.TargetOperators

"Build"
  ==> "CopyBinaries"
//   ==> "RunTests"
  ==> "NuGet"
  ==> "BuildPackage"
  ==> "All"

"Clean"
  ==> "Release"

"BuildPackage"
  ==> "PublishNuget"
  ==> "Release"

Target.RunOrDefault "All"