@{
    RootModule        = 'KillerScripts.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = 'b7e6f2a4-9c1d-4f3b-8a2e-5d6c7f8a9b01'
    Author            = 'Steve'
    CompanyName       = 'killertools.net'
    Copyright         = '(c) Steve. GPL-3.0.'
    Description       = 'Always-latest launcher for the killer-scripts repo. Each MSP field tool is pulled fresh from GitHub the moment you run it, with a local cache for offline use.'
    PowerShellVersion = '5.1'
    CompatiblePSEditions = @('Desktop', 'Core')

    FunctionsToExport = @(
        'Get-KillerScript',
        'Invoke-KillerScript',
        'Update-KillerScripts',
        'Invoke-AMORT',
        'Invoke-BEAM',
        'Invoke-BERET',
        'Invoke-DEBLOAT',
        'Invoke-DEFEND',
        'Invoke-DEPOT',
        'Invoke-FACTS',
        'Invoke-MACE',
        'Invoke-ODD',
        'Invoke-ORCA',
        'Invoke-PRINT',
        'Invoke-PRUNE',
        'Invoke-SHADE',
        'Invoke-STARE',
        'Invoke-TICK',
        'Invoke-URT',
        'Invoke-VITALS',
        'Invoke-WURSA'
    )

    AliasesToExport = @(
        'amort', 'beam', 'beret', 'debloat', 'defend', 'depot', 'facts', 'mace',
        'odd', 'orca', 'KillerPrint', 'prune', 'shade', 'stare', 'tick', 'urt',
        'vitals', 'wursa'
    )

    CmdletsToExport   = @()
    VariablesToExport = @()

    PrivateData = @{
        PSData = @{
            Tags       = @('MSP', 'sysadmin', 'Windows', 'PowerShell', 'scripts', 'field', 'killertools')
            LicenseUri = 'https://github.com/SteveTheKiller/killer-scripts/blob/main/LICENSE'
            ProjectUri = 'https://github.com/SteveTheKiller/killer-scripts'
        }
    }
}
