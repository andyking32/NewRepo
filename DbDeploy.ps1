param(
	[string]$targetEnvironment = "",
	[string]$connectionString = "",
	[string]$versionTable = "",
	[bool]$deployDatabase = $false
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


# Common vars
$scriptFolderPath = "{0}/Scripts" -f $PsScriptRoot 
$deploymentRootPath = (get-item $PsScriptRoot).parent.FullName


# Create connection object
$connection = New-Object System.Data.SqlClient.SqlConnection
$connection.ConnectionString = $connectionString
$connection.Open()


# Execute query to get latest version
$versionTable
$query = "IF EXISTS (SELECT * FROM sysobjects WHERE NAME = '{0}') select max(Number) from {0} else select 0" -f $versionTable
$command = $connection.CreateCommand()
$command.CommandText = $query
$currentVersion = $command.ExecuteScalar()

# Loop through scripts and get scripts which are greater than version and match environment
$sqlFiles = New-Object System.Collections.Generic.List[object]
foreach ($scriptFile in Get-ChildItem $scriptFolderPath -filter *.sql)
{
    $fileName = SplitFileName($scriptFile.Name)
    
    if (($fileName.Version -gt $currentVersion) -and ($fileName.Environment -eq $targetEnvironment -or $fileName.Environment -eq "all"))
    {
        $sqlFiles.Add($scriptFile)
    }
}


# If there are any sql files then create a script of all the files concatenated
if ($sqlFiles.Count -gt 0)
{
    $updateScript = "$deploymentRootPath/$currentVersion.$targetEnvironment.Update to latest version script.sql"
    New-item $updateScript -type file
    
    foreach ($sqlFile in $sqlFiles)
    {
        $fileName = SplitFileName($sqlFile.Name)
        
        $versionInsert = "insert into $versionTable select {0}, '{1}', '{2}', GETDATE()" -f $fileName.Version, $fileName.Environment, $fileName.Description
        $scriptContent = [IO.File]::ReadAllText("$scriptFolderPath\$sqlFile")
        
        Add-Content $updateScript "GO"
        Add-Content $updateScript $scriptContent
        Add-Content $updateScript "GO"
        Add-Content $updateScript $versionInsert

        if ($deployDatabase)
        {
            $command.CommandText = $scriptContent
            $command.ExecuteNonQuery()

            $command.CommandText = $versionInsert
            $command.ExecuteNonQuery()
        }
    }
}


# Cleanup
$command.Dispose()
$connection.Close()
$connection.Dispose()
