param(
	[string]$targetEnvironment = $(Throw "Please provide a target environment"),
	[string]$connectionString = $(Throw "Please provide a target environment"),
	[string]$versionTable = $(Throw "Please provide a version table"),
	[string]$scriptOutputPath = $(Throw "Please provide a script output path"),
	[bool]$deployDatabase = $false,
	[int]$version = 0
)


function SplitFileName($fileName)
{
    $fileParts = $fileName.Split('.')
    $version = [int]$fileParts[0]
    
    $fileName = new-object PSObject
    $fileName | add-member -type NoteProperty -Name Version -Value $version
    $fileName | add-member -type NoteProperty -Name Environment -Value $fileParts[1]
    $fileName | add-member -type NoteProperty -Name Description -Value $fileParts[2]
  
    return $fileName
}


$ErrorActionPreference = "Stop"


# Common vars
$scriptFolderPath = "{0}/Scripts" -f $PsScriptRoot 


# Create connection object
write-host ("Opening connection to {0}" -f $connectionString)
$connection = New-Object System.Data.SqlClient.SqlConnection
$connection.ConnectionString = $connectionString
$connection.Open()
$command = $connection.CreateCommand()


# Execute query to get latest version
if ($version -gt 0)
{
	write-host ("Database current version set to parameter value of {0}" -f $version)
	$currentVersion = $version
}
else
{
	$query = "IF EXISTS (SELECT * FROM sysobjects WHERE NAME = '{0}') select max(Number) from {0} else select 0" -f $versionTable
	write-host ("Executing query {0}" -f $query)
	$command.CommandText = $query
	$currentVersion = $command.ExecuteScalar()
	write-host ("Database current version is {0}" -f $currentVersion)
}


# Loop through scripts and get scripts which are greater than version and match environment
$sqlFiles = New-Object System.Collections.Generic.List[object]
foreach ($scriptFile in Get-ChildItem $scriptFolderPath -filter *.sql)
{
    $fileName = SplitFileName($scriptFile.Name)
    
    if (($fileName.Version -gt $currentVersion) -and ($fileName.Environment -eq $targetEnvironment -or $fileName.Environment -eq "all"))
    {
        write-host ("Adding script file {0}" -f $scriptFile)
		$sqlFiles.Add($scriptFile)
    }
}


# If there are any sql files then create a script of all the files concatenated
if ($sqlFiles.Count -gt 0)
{
	$timestamp = [System.DateTime]::UtcNow.ToString("yyyyMMddhhmmss")
	$targetEnvUpper = $targetEnvironment.ToUpper()
    $updateScript = "$scriptOutputPath/Version$currentVersion.$targetEnvUpper.Update_Script.$timestamp.sql"
    write-host ("Creating script file {0}" -f $updateScript)
	New-item $updateScript -type file
    
    foreach ($sqlFile in $sqlFiles)
    {
        $fileName = SplitFileName($sqlFile.Name)
        
        $versionInsert = "insert into $versionTable select {0}, '{1}', '{2}', GETDATE()" -f $fileName.Version, $fileName.Environment, $fileName.Description
        $scriptContent = [IO.File]::ReadAllText("$scriptFolderPath\$sqlFile")
        
        write-host ("Adding script '{0}' to {1}" -f $scriptContent, $updateScript)
		Add-Content $updateScript "GO"
        Add-Content $updateScript $scriptContent
        
		write-host ("Adding script '{0}' to {1}" -f $versionInsert, $updateScript)
		Add-Content $updateScript "GO"
        Add-Content $updateScript $versionInsert

        if ($deployDatabase)
        {
            write-host ("Executing script '{0}'" -f $scriptContent)
			$command.CommandText = $scriptContent
            $command.ExecuteNonQuery()

			write-host ("Executing script '{0}'" -f $versionInsert)
            $command.CommandText = $versionInsert
            $command.ExecuteNonQuery()
        }
    }
}


# Cleanup
write-host "Cleaning up db command and connection"
$command.Dispose()
$connection.Close()
$connection.Dispose()
