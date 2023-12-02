// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: Copyright 2022-2023 TautCony

namespace ISTA_Patcher.Core;

using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Serilog;

/// <summary>
/// A utility class for patching files and directories.
/// Contains the main part of the patching logic.
/// </summary>
internal static partial class PatchUtils
{    

    [EssentialPatch]
    public static int PatchIntegrityManager(ModuleDefMD module)
    {
        return module.PatchFunction(
            "\u0042\u004d\u0057.Rheingold.SecurityAndLicense.IntegrityManager",
            ".ctor",
            "()System.Void",
            DnlibUtils.EmptyingMethod
        );
    }

    [EssentialPatch]
    public static int PatchVerifyAssemblyHelper(ModuleDefMD module)
    {
        return module.PatchFunction(
            "\u0042\u004d\u0057.Rheingold.CoreFramework.InteropHelper.VerifyAssemblyHelper",
            "VerifyStrongName",
            "(System.String,System.Boolean)System.Boolean",
            DnlibUtils.ReturnTrueMethod
        );
    }
    
    [EssentialPatch]
    public static int PatchIstaProcessStarter(ModuleDefMD module)
    {
        return module.PatchFunction(
            "\u0042\u004d\u0057.Rheingold.CoreFramework.WcfCommon.IstaProcessStarter",
            "CheckSignature",
            "(System.String)System.Void",
            DnlibUtils.EmptyingMethod
        );
    }
         
    
}
