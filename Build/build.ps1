param(
	[string]$application = $(Throw "Please provide an application")
)

function checkLastExitCode($errorMessage)
{
	if ($LastExitCode -ne 0) { throw $errorMessage}
}

function restoreSharedAssemblyInfo()
{
	$sharedAssemblyInfoPath = "SharedAssemblyInfo.cs"
	$sharedAssemblyInfoBackupPath = "{0}.bak" -f $sharedAssemblyInfoPath
	
	Remove-Item $sharedAssemblyInfoPath
	Move-Item $sharedAssemblyInfoBackupPath $sharedAssemblyInfoPath
}

function updateAssemblyPatchVersion()
{
	$attributes = "AssemblyVersion", "AssemblyFileVersion", "AssemblyInformationalVersion"
	$sharedAssemblyInfoPath = "SharedAssemblyInfo.cs"
	$sharedAssemblyInfoBackupPath = "{0}.bak" -f $sharedAssemblyInfoPath
	
	Copy-Item $sharedAssemblyInfoPath $sharedAssemblyInfoBackupPath
	
	$content = Get-Content $sharedAssemblyInfoPath -Raw 
	foreach ($attribute in $attributes)
	{ 
		$attributeValue = if ($attribute -eq $attributes[-1]) { "$env:Build_Number-$env:Svn_Revision" } else { $env:Build_Number }
		$content = $content -replace ('(\[assembly: ' + $attribute + '\("(\d+\.){3})(\d+)("\)\])'), ('${1}' + $attributeValue + '$4')
	}
	
	Set-Content $sharedAssemblyInfoPath -Value $content -Encoding UTF8

	$assemblyInformationalVersionFound = $content -match ('\[assembly: AssemblyInformationalVersion\("(.*?)"\)\]')
	if (-not $assemblyInformationalVersionFound) { throw "AssemblyInformationalVersion not found in $sharedAssemblyInfoPath" }
	$assemblyInformationalVersion = $matches[1]

	return "v$assemblyInformationalVersion"
}


$ErrorActionPreference = "Stop"


# Get env vars
$solutionPath = (resolve-path -Path ".").Path
$buildDir = $env:BuildOutputDir
$source = $env:NuGetSource
$msBuildPath = $env:MsBuild14Path
$nUnitTestResultFile = "$buildDir\$env:NUnitTestResultFile"
$coberturaResultFile = "$buildDir\$env:CoberturaResultFile"
$openCoverResultsFile = "$buildDir\$env:OpenCoverResultFile"
$artifactsZipContentsFolder = "$solutionPath\$buildDir/_ZipContents"
$artifactZipFolder = "$solutionPath\$env:ArtifactZipFolder"


# Get project specific vars from settings file
$buildSettings = Get-Content -Raw -Path "$PSScriptRoot\buildSettings.json" | ConvertFrom-Json
$applicationSettings = $buildSettings.applications.$application
$solutionName = $applicationSettings.solutionName
$artifactFolders = $applicationSettings.artifactFolders
$unitTestFilePattern = $applicationSettings.unitTestFilePattern
$openCoverIncludeFilter = $applicationSettings.openCoverIncludeFilter
$openCoverExcludeFilter = $applicationSettings.openCoverExcludeFilter
$aimReleaseableUnit = $applicationSettings.aimReleaseableUnit


# Restore nuget packages
./.nuget/NuGet.exe restore -source $source
checkLastExitCode("NuGet restore failed")


# Update shared assembly patch version
$assemblyInformationalVersion = updateAssemblyPatchVersion


# Run msbuild
&"$msBuildPath" $solutionName.sln /v:minimal /p:Configuration=Prod /p:OutDir="../$buildDir" /p:GenerateProjectSpecificOutputFolder=true
checkLastExitCode("MsBuild failed")


# Collect test dlls and run nunit console runner via open cover to get code coverage report
$testDlls = Get-ChildItem $buildDir\$unitTestFilePattern | Foreach-Object { "\""$_\""" }
.\packages\OpenCover.4.6.519\tools\OpenCover.Console.exe -register:path64 -output:$openCoverResultsFile -filter:"$openCoverIncludeFilter$openCoverExcludeFilter" `
														-target:".\packages\NUnit.ConsoleRunner.3.5.0\tools\nunit3-console.exe" "-targetargs:$testDlls --result \""$nUnitTestResultFile;format=nunit2\"""
checkLastExitCode("OpenCover failed")
.\packages\OpenCoverToCoberturaConverter.0.2.4.0\tools\OpenCoverToCoberturaConverter.exe -input:$openCoverResultsFile -output:$coberturaResultFile
checkLastExitCode("OpenCoverToCoberturaConverter failed")


# Restore SharedAssemblyInfo.cs.bak
restoreSharedAssemblyInfo


# Create a zip of artifacts
New-Item $artifactsZipContentsFolder -type directory
New-Item $artifactZipFolder -type directory
$artifactFolders | Foreach-Object { copy-item $_.Replace('$buildDir', $buildDir) -Destination $artifactsZipContentsFolder -Recurse }
Remove-Item -Recurse -Force $artifactsZipContentsFolder\**\.svn
add-type -AssemblyName "System.IO.Compression.FileSystem"
[System.IO.Compression.ZipFile]::CreateFromDirectory($artifactsZipContentsFolder, "$artifactZipFolder\$solutionName-$assemblyInformationalVersion.zip")
