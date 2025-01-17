// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: Copyright 2023 TautCony

namespace ISTA_Patcher.Core;

using System.Diagnostics;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using ISTA_Patcher.Core.Patcher;
using Serilog;

public static partial class Patch
{
    public static string OutputDirName { get; set; } = "@ista-patched";

    public static string BakDirName { get; set; } = "@ista-backup";

    public static void PatchISTA(IPatcher patcher, ProgramArgs.PatchOptions options)
    {
        Log.Information("=== ISTA Patch Begin ===");
        var timer = Stopwatch.StartNew();

        var guiBasePath = Constants.TesterGUIPath.Aggregate(options.TargetPath, Path.Join);
        var pendingPatchList = patcher.GeneratePatchList(options.TargetPath);
        var indentLength = pendingPatchList.Select(i => i.Length).Max() + 1;
        var patchAppliedCount = new int[patcher.Patches.Count];

        var lcts = new LimitedConcurrencyLevelTaskScheduler(Environment.ProcessorCount);
        var factory = new TaskFactory(lcts);
        var tasks = new List<Task>();
        using (var cts = new CancellationTokenSource())
        {
            tasks.AddRange(pendingPatchList.Select(item =>
                factory.StartNew(
                    () => PatchSingleFile(item, patchAppliedCount, guiBasePath, indentLength, patcher, options),
                    cts.Token)
                )
            );

            Task.WaitAll(tasks.ToArray());
        }

        foreach (var line in BuildIndicator(patcher.Patches, patchAppliedCount))
        {
            Log.Information("{Indent}{Line}", new string(' ', indentLength), line);
        }

        if (options.GenerateMockRegFile)
        {
            Log.Information("=== Generating mock reg file ===");
            PatchUtils.GenerateMockRegFile(guiBasePath);
        }

        timer.Stop();
        Log.Information("=== ISTA Patch Done in {Time:mm\\:ss} ===", timer.Elapsed);
    }

    private static void PatchSingleFile(string pendingPatchItem, IList<int> patchAppliedCount, string guiBasePath, int indentLength, IPatcher patcher, ProgramArgs.PatchOptions options)
    {
        var pendingPatchItemFullPath = pendingPatchItem.StartsWith('!')
            ? Path.Join(options.TargetPath, pendingPatchItem.Trim('!'))
            : Path.Join(guiBasePath, pendingPatchItem);

        var originalDirPath = Path.GetDirectoryName(pendingPatchItemFullPath);
        var patchedDirPath = Path.Join(originalDirPath, OutputDirName);
        var patchedFileFullPath = Path.Join(patchedDirPath, Path.GetFileName(pendingPatchItem));
        var bakDirPath = Path.Join(originalDirPath, BakDirName);
        var bakFileFullPath = Path.Join(bakDirPath, Path.GetFileName(pendingPatchItem));

        if (File.Exists(patchedFileFullPath))
        {
            File.Delete(patchedFileFullPath);
        }

        var indent = new string(' ', indentLength - pendingPatchItem.Length);
        if (!File.Exists(pendingPatchItemFullPath))
        {
            Log.Information(
                "{Item}{Indent}{Result} [not found]",
                pendingPatchItem,
                indent,
                string.Concat(Enumerable.Repeat("*", patcher.Patches.Count)));
            return;
        }

        Directory.CreateDirectory(patchedDirPath);
        Directory.CreateDirectory(bakDirPath);

        try
        {
            if (options.Restore && File.Exists(bakFileFullPath))
            {
                Log.Debug("Backup detected, restoring {Item}", pendingPatchItem);
                File.Copy(bakFileFullPath, pendingPatchItemFullPath, true);
            }

            var module = PatchUtils.LoadModule(pendingPatchItemFullPath);
            var patcherVersion = PatchUtils.HavePatchedMark(module);
            var isPatched = patcherVersion != null;
            if (isPatched && !options.Force)
            {
                Log.Information(
                    "{Item}{Indent}{Result} [already patched by {Version}]",
                    pendingPatchItem,
                    indent,
                    string.Concat(Enumerable.Repeat("*", patcher.Patches.Count)),
                    patcherVersion);
                return;
            }

            // Patch and print result
            var result = patcher.Patches.Select(patch => patch(module)).ToList();
            result.Select((item, index) => (item, index)).ToList()
                  .ForEach(patch => patchAppliedCount[patch.index] += patch.item);

            isPatched = result.Exists(i => i > 0);
            var resultStr = result.Aggregate(string.Empty, (c, i) => c + (i > 0 ? i.ToString("X") : "-"));

            // Check if at least one patch has been applied
            if (!isPatched)
            {
                Log.Information("{Item}{Indent}{Result} [skip]", pendingPatchItem, indent, resultStr);
                return;
            }

            if (!File.Exists(bakFileFullPath))
            {
                Log.Debug("Bakup file {BakFileFullPath} does not exist, copy...", bakFileFullPath);
                File.Copy(pendingPatchItemFullPath, bakFileFullPath, false);
            }

            PatchUtils.SetPatchedMark(module);
            PatchUtils.SaveModule(module, patchedFileFullPath);

            Log.Debug("Patched file {PatchedFileFullPath} created", patchedFileFullPath);
            var patchedFunctionCount = result.Aggregate(0, (c, i) => c + i);

            // Check if need to deobfuscate
            if (!options.Deobfuscate)
            {
                Log.Information("{Item}{Indent}{Result} [{PatchedFunctionCount} func patched]", pendingPatchItem, indent, resultStr, patchedFunctionCount);
                return;
            }

            DeObfuscateSingleFile(pendingPatchItem, patchedFileFullPath, patchedFunctionCount, indent, resultStr);
        }
        catch (Exception ex)
        {
            Log.Information(
                "{Item}{Indent}{Result} [failed]: {Reason}",
                pendingPatchItem,
                indent,
                string.Concat(Enumerable.Repeat("*", patcher.Patches.Count)),
                ex.Message);
            Log.Debug("ExceptionType: {ExceptionType}, StackTrace: {StackTrace}", ex.GetType().FullName, ex.StackTrace);

            if (File.Exists(patchedFileFullPath))
            {
                File.Delete(patchedFileFullPath);
            }
        }
    }

    private static void DeObfuscateSingleFile(string pendingPatchItem, string patchedFileFullPath, int patchedFunctionCount, string indent, string resultStr)
    {
        try
        {
            var deobfTimer = Stopwatch.StartNew();

            var deobfPath = patchedFileFullPath + ".deobf";
            PatchUtils.DeObfuscation(patchedFileFullPath, deobfPath);
            if (File.Exists(patchedFileFullPath))
            {
                File.Delete(patchedFileFullPath);
            }

            File.Move(deobfPath, patchedFileFullPath);

            deobfTimer.Stop();
            var timeStr = deobfTimer.ElapsedTicks > Stopwatch.Frequency
                ? $" in {deobfTimer.Elapsed:mm\\:ss}"
                : string.Empty;
            Log.Information(
                "{Item}{Indent}{Result} [{PatchedFunctionCount} func patched][deobfuscate success{Time}]",
                pendingPatchItem,
                indent,
                resultStr,
                patchedFunctionCount,
                timeStr);
        }
        catch (ApplicationException ex)
        {
            Log.Information(
                "{Item}{Indent}{Result} [{PatchedFunctionCount} func patched][deobfuscate skipped]: {Reason}",
                pendingPatchItem,
                indent,
                resultStr,
                patchedFunctionCount,
                ex.Message);
        }
    }

    private static IEnumerable<string> BuildIndicator(IReadOnlyCollection<Func<ModuleDefMD, int>> patches, IReadOnlyList<int> counting)
    {
        return patches
               .Select(FormatName)
               .Reverse()
               .AsEnumerable()
               .Select((name, idx) =>
               {
                   var revIdx = patches.Count - 1 - idx;
                   return $"{new string('│', revIdx)}└{new string('─', idx)}>[{name}: {counting[revIdx]}]";
               });

        string FormatName(Func<ModuleDefMD, int> func)
        {
            var name = func.Method.Name;
            var match = ActionNamePattern().Match(name);
            if (match.Success)
            {
                name = match.Groups[1].Value;
            }

            return name.StartsWith("Patch") ? name[5..] : name;
        }
    }

    [GeneratedRegex("^<([^>]+)>", RegexOptions.Compiled)]
    private static partial Regex ActionNamePattern();
}
