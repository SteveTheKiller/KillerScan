@{
    RootModule        = 'KillerScan.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = 'c4d1a7f2-8b63-4e51-9d0a-2f7b5c6e8143'
    Author            = 'Steve'
    CompanyName       = 'killertools.net'
    Copyright         = '(c) Steve. GPL-3.0.'
    Description       = 'Scan the network from the KillerScan terminal and get objects back. Wraps the KillerScan command line and turns its JSON into PowerShell objects you can filter, sort and pipe.'
    PowerShellVersion = '5.1'
    CompatiblePSEditions = @('Desktop', 'Core')

    FunctionsToExport = @(
        'scan',
        'probe',
        'vendor',
        'netinfo'
    )
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()

    PrivateData = @{
        PSData = @{
            Tags       = @('KillerScan', 'network', 'scanner')
            LicenseUri = 'https://github.com/SteveTheKiller/KillerScan/blob/main/LICENSE'
            ProjectUri = 'https://killerscan.net'
        }
    }
}
