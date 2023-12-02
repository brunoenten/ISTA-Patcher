// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: Copyright 2023 TautCony

namespace ISTA_Patcher.Core.Patcher;

public class DefaultLicensePatcher : DefaultPatcher
{
    public DefaultLicensePatcher(string modulus, string exponent, ProgramArgs.LicenseOptions opts)
        : base(opts)
    {        
    }
}
