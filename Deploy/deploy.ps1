param(
	[string]$application = $(Throw "Please provide an application"),
	[string]$environment = $(Throw "Please provide an environment"),
	[bool]$deployDatabase = $(Throw "Please specify if database deployment is required"))


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


$ErrorActionPreference = "Stop"


Try
{
	#Ensure file runs from script path
	Set-Location $PsScriptRoot
	$deploymentRootPath = (Get-Item $PsScriptRoot).parent.FullName
	$deploymentArtifacts = $PsScriptRoot


	# Common vars
	$deploySettings = Get-Content -Raw -Path "$deploymentArtifacts\deploySettings.json" | ConvertFrom-Json
	$applicationSettings = $deploySettings.applications.$application
	$environmentSettings = $deploySettings.environments.$environment
	$isPrincipalServer = $env:computername -eq $environmentSettings.prinicpalServerName


	# Load Xml transform dll
	$xmlTransformPath = get-childitem -path "MyApp.*.Api\bin\Microsoft.Web.XmlTransform.dll"
	$bytes = [System.IO.File]::ReadAllBytes($xmlTransformPath)
	[System.Reflection.Assembly]::Load($bytes)


	# Stop app pool
	$appPoolState = Get-WebAppPoolState $applicationSettings.appPool
	if ($appPoolState.Value -ne "Stopped")
	{
		write-host ("Stopping app pool {0} at {1}" -f $applicationSettings.appPool, ([System.DateTime]::UtcNow))
		Stop-WebAppPool $applicationSettings.appPool

		while ((Get-WebAppPoolState $applicationSettings.appPool).Value -ne "Stopped")
		{
			Write-Host ("App pool state at {0} is {1}" -f ([System.DateTime]::UtcNow), (Get-WebAppPoolState $applicationSettings.appPool).Value)
			Start-Sleep -Seconds 1
		}
		
        Write-Host ("App pool stopped at {0}" -f ([System.DateTime]::UtcNow))
	}


	# Disable Scheduled tasks
	if ($isPrincipalServer)
	{
		ForEach ($task in $applicationSettings.scheduledTasks)
		{   
			$currentTask = Get-ScheduledTask -TaskName $task.Name
	
			if ($currentTask.State -eq "Running") 
			{ 
				throw "Cannot deploy while task:" + $task.name + " running" 
			}
	    
			write-host ("Disabling task {0}" -f $task.name)
			Disable-ScheduledTask $task.name
		}
	}


	# Applying config transforms
	$configs = get-childitem $deploymentArtifacts -include *.config -recurse | Where-Object {$_.Name -match "^\w+?.config$"}
	foreach ($config in $configs) 
	{
		$replacementFile = $config.DirectoryName + "\" + $config.BaseName + "." + $environment + $config.Extension
	
		if (-not (Test-Path $replacementFile)) { continue }

		write-host ("Transforming {0} with {1}" -f $config.FullName, $replacementFile)
		XmlDocTransform $config.FullName $replacementFile

		Get-ChildItem $config.DirectoryName | Where{$_.Name -Match ("^" + $config.BaseName + "\.\w+?\.config")} | Remove-Item

		if ($config.Name -eq "app.config")
		{	
			$exeConfig = Get-ChildItem ($config.DirectoryName + "\*.exe.config")
		
			if (!$exeConfig)
			{
				continue
			}

			Remove-Item $exeConfig.FullName
			Rename-Item $config.FullName $exeConfig.Name
		}
	}


	# Remove all current files
	write-host ("Removing all files from {0}" -f $deploymentRootPath)
	Get-ChildItem $deploymentRootPath -Directory -Exclude (Get-Item $deploymentArtifacts).Name | Remove-Item -Recurse -Force


	# Copy all deploy files up one level
	write-host ("Copying all files to {0}" -f $deploymentRootPath)
	Get-ChildItem 'MyApp.*' -Directory | Move-Item -Destination $deploymentRootPath -Force 


	# Start app pool
	write-host ("Starting app pool {0}" -f $applicationSettings.appPool)
	Start-WebAppPool $applicationSettings.appPool
	

	# Enable scheduled tasks
	if ($isPrincipalServer)
	{
		Foreach ($task in $applicationSettings.scheduledTasks)
		{  
			write-host ("Enabling task {0}" -f $task.name)
			Enable-ScheduledTask $task.Name
		}
	}


	# Deploy database
	if ($environmentSettings.generateDbScript -and $isPrincipalServer)
	{
		$appName = $applicationSettings.appPool -replace "Api", ""
		$scriptOutputPath = "{0}{1}" -f $environmentSettings.dbScriptOutputPath, $appName
		./databaseDeploy.ps1 $environment $environmentSettings.connectionString $applicationSettings.versionTable $scriptOutputPath $deployDatabase
	}
}
Catch
{
    throw
}
Finally
{
	# Move up one level and delete artifacts temp folder
	Set-Location $deploymentRootPath
	write-host ("Removing all files from {0}" -f $deploymentArtifacts)
	Remove-Item $deploymentArtifacts -Force -Recurse
}
