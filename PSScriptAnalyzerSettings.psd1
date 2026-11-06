@{
    Rules = @{
        PSPlaceOpenBrace           = @{
            Enable             = $true
            OnSameLine         = $true
            NewLineAfter       = $true
            IgnoreOneLineBlock = $false
        }

        PSPlaceCloseBrace          = @{
            Enable             = $true
            NewLineBefore      = $false
            NewLineAfter       = $false
            IgnoreOneLineBlock = $false
        }

        PSUseConsistentIndentation = @{
            Enable              = $true
            IndentationSize     = 4
            Kind                = 'space'
            # Extension default (powershell.codeFormatting.pipelineIndentation =
            # 'noIndentation'); 'IncreaseIndentationForFirstPipeline' is the
            # PSScriptAnalyzer default if you prefer indented pipelines.
            PipelineIndentation = 'NoIndentation'
        }

        PSUseConsistentWhitespace  = @{
            Enable                                  = $true
            CheckInnerBrace                         = $true
            CheckOpenBrace                          = $true
            CheckOpenParen                          = $true
            CheckOperator                           = $true
            CheckPipe                               = $true
            CheckPipeForRedundantWhitespace         = $false
            CheckSeparator                          = $true
            CheckParameter                          = $false
            IgnoreAssignmentOperatorInsideHashTable = $true
        }

        PSAlignAssignmentStatement = @{
            Enable                                  = $true
            CheckHashtable                          = $true
            AlignHashtableKvpWithInterveningComment = $true
            CheckEnum                               = $true
            AlignEnumMemberWithInterveningComment   = $true
            IncludeValuelessEnumMembers             = $true
        }
    }
}
