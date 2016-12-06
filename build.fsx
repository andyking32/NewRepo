#I "../../FAKE/tools"
#I "../../FSharp.Data/lib/net40"

#I "../../packages/FAKE/tools"
#I "../../packages/FSharp.Data/lib/net40"

#r "FakeLib.dll"
#r "FSharp.Data.dll"

open Fake
open Fake.Paket
open Fake.VSTest
open Fake.Git
open FSharp.Data
open System
open System.IO


// 1. Declare types to be used
type Settings = JsonProvider<"settings.templates.json", SampleIsList = true>

type BuildNumberProperties = { 
    DateString: string
    BuildDate: DateTime
    DailyCounter:int
}
with
    member this.DateDaysSinceUnixTime = (this.BuildDate - DateTime(1970, 1, 1)).Days


// 2. Set params from the calling application and environment
let skipTests = getBuildParamOrDefault "skipTests" "False" |> Boolean.Parse
let buildNumber = environVarOrFail "BUILD_BUILDNUMBER"
let sourceBranch = environVarOrFail "BUILD_SOURCEBRANCHNAME"
let definitionName = environVarOrFail "BUILD_DEFINITIONNAME"


// 3. Declared commonly variables to be used during the script
let settings = Settings.Load(currentDirectory </> ".build/settings.json")
let commitHash = (getSHA1 "." "HEAD").Substring(0, 10)
let buildDir = currentDirectory </> "_BuildScriptOutput"
let artifactsDir = buildDir </> "_Artifacts"
let sharedAssemblyInfoCSharpFileName = "SharedAssemblyInfo.cs"
let sharedAssemblyInfoFileNames = [sharedAssemblyInfoCSharpFileName; "SharedAssemblyInfo.fs"] |> List.filter File.Exists
let sharedAssemblyInfoBakFileNames = sharedAssemblyInfoFileNames |> List.map (fun n -> n + ".bak")
let assemblyInformationalVersionName = "AssemblyInformationalVersion"
let versionAttributesNames = [assemblyInformationalVersionName; "AssemblyVersion"; "AssemblyFileVersion"]
let solutionFile = settings.Project + ".sln"
let unquotedVersion (version: string) = version.Replace("\"","")
let publishedWebsitesPath = "_PublishedWebsites"
    

// 4. Process the build number params to get daily build number, build date and number of days since unix time (01/01/1970) properties
let buildNumberProperties =
    let buildNumberParts = buildNumber.Split('.')
    
    match buildNumberParts with
    | [|dateStr;dailyCounter|] -> 
        {
            DateString = dateStr 
            BuildDate = DateTime.ParseExact(dateStr, "yyyyMMdd", Globalization.CultureInfo.InvariantCulture)
            DailyCounter = int dailyCounter
        } 
    | _ -> failwithf "build number is incorrect format"


// 5. Declare the targets
Target "Clean" (fun _ ->
    CleanDir buildDir)

Target "RestorePackages" (fun _ ->
    Restore id)

Target "CopySharedAssemblyInfo" (fun _ ->
    ActivateFinalTarget "RenameSharedAssemblyInfo"
    
    if sharedAssemblyInfoFileNames |> List.isEmpty then
        failwith "No SharedAssemblyInfo.(cs|fs) found"

    sharedAssemblyInfoBakFileNames |> List.iter DeleteFile

    List.zip sharedAssemblyInfoBakFileNames sharedAssemblyInfoFileNames
    |> List.iter (fun (bak, original) -> CopyFile bak original))

Target "SetSharedAssemblyVersion" (fun _ ->
    let getAssemblyVersion assemblyInfoPath attributeName = 
        let version = AssemblyInfoFile.GetAttributeValue attributeName assemblyInfoPath
        match version with
        | None -> failwithf "AssemblyInfo file %s has no %s attribute" assemblyInfoPath attributeName
        | Some v -> v

    let updateVersion (version: string) isInformational = 
        let semVerInfo = SemVerHelper.parse (unquotedVersion version)
        if isInformational then
            sprintf "\"%d.%d.%s-%s-%s_%d.%s\"" semVerInfo.Major semVerInfo.Minor sourceBranch definitionName buildNumberProperties.DateString buildNumberProperties.DailyCounter commitHash
        else
            sprintf "\"%d.%d.%d.%d\"" semVerInfo.Major semVerInfo.Minor buildNumberProperties.DateDaysSinceUnixTime buildNumberProperties.DailyCounter

    let getVersion filePath attributeName = 
        let versionString = getAssemblyVersion filePath attributeName 
        let version = updateVersion versionString
        let newVersion = 
            match attributeName with
            | "AssemblyInformationalVersion" -> version true
            | _ -> version false  
        AssemblyInfoFile.Attribute(attributeName, newVersion, "")
    
    let attributes =
        sharedAssemblyInfoFileNames
        |> List.map (fun fileName -> fileName, versionAttributesNames |> List.map (getVersion fileName))
    
    attributes
    |> List.iter (fun (fileName, attrs) -> AssemblyInfoFile.UpdateAttributes fileName attrs))

Target "BuildSolution" (fun _ ->
    MSBuild
        buildDir
        "Build"
            [
                ("OutDir", buildDir)
                ("Configuration", settings.Configuration)
                ("GenerateProjectSpecificOutputFolder", "true")
                ("UseWPP_CopyWebApplication", "true")
                ("PipelineDependsOnBuild", "false")
            ]
        [solutionFile]
    |> ignore

    // MSBuild won't copy web.*.configs to published websites when using UseWPP_CopyWebApplication
    // This is a workaround to copy them from the output directory after build
    !! (buildDir </> "**" </> publishedWebsitesPath </> "*/bin/*.config")
    |> Seq.iter (fun configFile ->
                    let fileInfo = fileInfo configFile
                    MoveFile fileInfo.Directory.Parent.FullName configFile))

Target "RunTests" (fun _ ->
    if not skipTests then 
        !! (buildDir </> settings.Project + ".*/**/" + settings.Tests.TestDllMatcher)
        |> Seq.distinctBy(fun f -> (fileInfo f).Name)
        |> VSTest (fun p ->
            { p with 
                TestAdapterPath = buildDir
                TestCaseFilter = settings.Tests.TestCaseFilter |> Option.toObj
                Logger = "trx" }))

Target "RenameSharedAssemblyInfo" (fun _ ->
    sharedAssemblyInfoFileNames |> List.iter DeleteFile

    List.zip sharedAssemblyInfoFileNames sharedAssemblyInfoBakFileNames
    |> List.iter (fun (original, bak) -> Rename original bak))

Target "ZipArtifacts" (fun _ ->
    let version = AssemblyInfoFile.GetAttributeValue assemblyInformationalVersionName sharedAssemblyInfoCSharpFileName
    let filename = (sprintf @"%s\%s %s.zip" artifactsDir settings.Project (unquotedVersion version.Value))
    let filePattern = !! ("**\*.*")
    let paketDependencies = currentDirectory </> "paket.dependencies"
    let paketExe = currentDirectory </> ".paket/paket.exe"
    let deploySettings = currentDirectory </> ".deploy/settings.json"

    CreateDir artifactsDir
    
    CopyFile artifactsDir paketExe
    CopyFile artifactsDir paketDependencies
    if fileExists deploySettings then CopyFile artifactsDir deploySettings
    
    let codeArtifacts =
        settings.Artifacts
        |> Array.toList
        |> List.map (fun artifact -> 
            if FileHelper.TestDir (buildDir </> artifact </> publishedWebsitesPath) then
                artifact, SetBaseDir (buildDir </> artifact </> publishedWebsitesPath </> artifact) filePattern
            else
                artifact, SetBaseDir (buildDir </> artifact) filePattern)

    let artifacts =
        match settings.DatabaseScriptsFolder with
        | Some dbScriptsFolder -> ("DatabaseMigrationScripts", SetBaseDir dbScriptsFolder !! "*.sql") :: codeArtifacts
        | None -> codeArtifacts
    
    ZipOfIncludes filename artifacts)


 // 6. Declare target dependency tree
"Clean"
    ==> "RestorePackages"
    ==> "CopySharedAssemblyInfo"
    ==> "SetSharedAssemblyVersion"
    ==> "BuildSolution"
    ==> "RunTests"
    ==> "ZipArtifacts"
    

// 7. Run the default target
RunTargetOrDefault "ZipArtifacts"