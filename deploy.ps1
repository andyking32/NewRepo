param(
		[bool]$reinstallServices = $(Throw "Please provide whether to reinstall services"),
		[bool]$pinBuildAndRelease = $(Throw "Please provide whether to pin the build and release"),
		[string]$buildId = $(Throw "Please provide a build id"),
		[string]$releaseId = $(Throw "Please provide a release id"),
		[string]$configuration = $(Throw "Please provide a configuration"),
		[string]$adminUsername = $(Throw "Please provide an admin username"),
		[string]$adminPassword = $(Throw "Please provide an admin password"),
		[string]$serviceUsername = $(Throw "Please provide a service username"),
		[string]$servicePassword = $(Throw "Please provide a service password")
	)



Add-Type -AssemblyName System.IO.Compression.FileSystem
$bytes = [System.IO.File]::ReadAllBytes("$PSScriptRoot\Microsoft.Web.XmlTransform.dll")
[System.Reflection.Assembly]::Load($bytes)

function XmlDocTransform($xml, $xdt)
{
	$xmldoc = New-Object Microsoft.Web.XmlTransform.XmlTransformableDocument;
	$xmldoc.PreserveWhitespace = $true
	$xmldoc.Load($xml);

	$transf = New-Object Microsoft.Web.XmlTransform.XmlTransformation($xdt);

	if ($transf.Apply($xmldoc) -eq $false)
	{
		throw "Transformation failed."
	}
	
	$xmldoc.Save($xml);
}


#1. Define variables
$ErrorActionPreference = "Stop"
$settingsFile = $PSScriptRoot + "\" + "settings.json"
$settings = Get-Content -Raw -Path $settingsFile | ConvertFrom-Json
$artifactZipName = get-childitem $PSScriptRoot -Filter *.zip -Name
$artifactLocalPath = $PSScriptRoot + "\" + $artifactZipName
$unzipTempFolder = $PSScriptRoot + "\_unzip"
$secureAdminPassword = ConvertTo-SecureString $adminPassword -AsPlainText -Force
$adminCredential = New-Object Management.Automation.PSCredential ($adminUsername, $secureAdminPassword)
$dbmFolder = "DatabaseMigrations"

#2. Stop and uninstall service(s)
write-verbose "Stopping and uninstalling service(s)"
foreach ($project in $settings.services)
{
	foreach ($servicename in $project.servicenames)
	{
		$service = get-wmiobject win32_service -filter "name='$servicename'"

		if ($service) 
		{ 
			write-verbose "Stopping service $servicename"
			$service.stopservice()
			
			if ($reinstallServices)
			{
				write-verbose "Uninstalling service $servicename"
				$service.delete()
			}
		 }
	}
}


#3 Stop app pools
write-verbose "Stopping app pools"
foreach ($appPool in $settings.appPools)
{
	# Note: we can't use Stop-WebAppPool as it prevents further write-verbose messages from appearing in the logs for some reason
	write-verbose "Stopping app pool $appPool"
	iex ($env:windir + "\system32\inetsrv\appcmd stop apppool /apppool.name:" + $appPool)
}


#4. Unzip artifact file to temp folder
write-verbose "Unzipping artifactLocalPath"
if (Test-Path $unzipTempFolder) { Remove-Item $unzipTempFolder -Force -Recurse }
[System.IO.Compression.ZipFile]::ExtractToDirectory($artifactLocalPath, $unzipTempFolder);


#5. Rename app_offline.htm.bak to app_offline.htm
write-verbose "Renaming app_offline.htm.bak to app_offline.htm"
$appOfflines = get-childitem $unzipTempFolder -include app_offline.htm.bak -recurse
foreach ($appOffline in $appOfflines) { rename-item $appOffline $appOffline.Name.Replace(".bak", "") }


#6 Rename web.configs and remove other ones
write-verbose "Applying config transforms and removing unneeded ones"
$configs = get-childitem $unzipTempFolder -include *.config -recurse | Where-Object {$_.Name -match "^\w+?.config$"}
foreach ($config in $configs) 
{
	$replacementFile = $config.DirectoryName + "\" + $config.BaseName + "." + $configuration + $config.Extension
	
	if (-not (Test-Path $replacementFile)) { continue }

	write-verbose ("Applying transform: " + $replacementFile + " to " + $config.FullName)
	XmlDocTransform $config.FullName $replacementFile

	write-verbose "Removing other config files"
	Get-ChildItem $config.DirectoryName | Where{$_.Name -Match ("^" + $config.BaseName + "\.\w+?\.config")} | Remove-Item

	if ($config.Name -eq "app.config")
	{	
		$exeConfig = Get-ChildItem ($config.DirectoryName + "\*.exe.config")
		
		if (!$exeConfig)
		{
			continue
		}

		write-verbose ("Removing " + $exeConfig.Name + " and renaming app.config to " + $exeConfig.Name)
		Remove-Item $exeConfig.FullName
		Rename-Item $config.FullName $exeConfig.Name
	}
}


#7. Clean out the contents of each root subfolder which matches the folders of the artifact unzip folder and copy content from unzip folder
write-verbose "Removing content from artifact folders and then copying from unzip folder"
$artifactSubfolders = Get-ChildItem $unzipTempFolder
foreach ($artifactSubfolder in $artifactSubfolders)
{
	write-verbose ("Removing content from " + $PSScriptRoot + "\" + $artifactSubfolder.Name)
	Remove-Item ($PSScriptRoot + "\" + $artifactSubfolder.Name + "*") -recurse -Force
}
write-verbose ("Moving content from $unzipTempFolder to $PSScriptRoot")
Move-Item -Path ("$unzipTempFolder\*") -Destination $PSScriptRoot -include * -Force


#8 Install and start service(s)
write-verbose "Installing and starting service(s)"
foreach ($project in $settings.services)
{
	$projectName = $project.projectName

	if ($reinstallServices)
	{
		write-verbose "Installing services in \$projectName\$projectName.exe"
		iex ($PSScriptRoot + "\$projectName\$projectName.exe install $serviceUsername $servicePassword")
	}

	foreach ($serviceName in $project.serviceNames)
	{
		$service = Get-WmiObject Win32_Service -filter "name='$serviceName'"

		if (-not $service) { Throw "The service $serviceName does not exist" }

		write-verbose "Starting service $serviceName"
		$service.StartService()
	}
}


#9 Reset iis (which also starts app pools)
write-verbose "Resetting IIS"
iisreset


#10 Set the TFS build to be kept indefinitely
if ($pinBuildAndRelease)
{
	write-verbose "Setting TFS build id $buildId to be kept indefinitely"
	$buildApiUri = $settings.tfsUrl + "_apis/build/builds/" + $buildId + "?api-version=2.0"
	Invoke-RestMethod -Uri $buildApiUri -Method patch -Body "{keepForever:true}" -ContentType "application/json" -Credential $adminCredential
}


#11 Set the TFS release to be kept indefinitely
if ($pinBuildAndRelease)
{
	write-verbose "Setting TFS release id $releaseId to be kept indefinitely"
	$releaseApiUri = $settings.tfsUrl + "_apis/release/releases/" + $releaseId + "?api-version=1.0"
	Invoke-RestMethod -Uri $releaseApiUri -Method patch -Body "{keepForever:true}" -ContentType "application/json" -Credential $adminCredential
}


#12 Clean up unneeded files
write-verbose "Clean up artifact files"
Remove-Item $unzipTempFolder -Force -Recurse
remove-item $settingsFile
remove-item $artifactLocalPath
remove-item "$PSScriptRoot\deploy.ps1"
remove-item "$PSScriptRoot\Microsoft.Web.XmlTransform.dll"
if (Test-Path $dbmFolder) { Remove-Item $dbmFolder -Force -Recurse }


#13. Rename app_offline.htm to app_offline.htm.bak
write-verbose "Renaming app_offline.htm to app_offline.htm.bak"
$appOfflines = get-childitem $PSScriptRoot -include app_offline.htm -recurse
foreach ($appOffline in $appOfflines) { rename-item $appOffline ($appOffline.Name + ".bak") }
