using System;
using System.IO;

namespace WeTypeSkinStudio
{
    internal static class NativeStateChecks
    {
        internal static void Run(string output)
        {
            Directory.CreateDirectory(output);
            var originalStore = NativePatchStore.Installed();
            byte[] app = File.ReadAllBytes(originalStore.BackupPath);
            byte[] asset = File.ReadAllBytes(originalStore.AssetBackupPath);
            byte[] toolbar = File.ReadAllBytes(NativeToolbarStore.Installed().BackupPath);
            NativeSkinPatch.RequireOriginal(app, NativeSkinPatch.Version);
            NativeSkinPatch.RequireOriginalAsset(asset);
            NativeToolbarPatch.RequireOriginal(toolbar);
            string fixture = Path.Combine(output, "fixture"); Directory.CreateDirectory(fixture);
            string appPath = Path.Combine(fixture, "app.so"), assetPath = Path.Combine(fixture, "background.gif"), toolbarPath = Path.Combine(fixture, "toolbar.exe");
            var candidate = new NativePatchStore(appPath, assetPath, Path.Combine(fixture, "backups"));
            var statusBar = new NativeToolbarStore(toolbarPath, Path.Combine(fixture, "backups"));
            Directory.CreateDirectory(Path.GetDirectoryName(candidate.BackupPath));
            File.WriteAllBytes(candidate.BackupPath, app); File.WriteAllBytes(candidate.AssetBackupPath, asset); File.WriteAllBytes(statusBar.BackupPath, toolbar);
            SkinTheme purple = SkinTheme.Presets()[5];
            PatchPlan plan = NativeSkinPatch.Create(app, purple, NativeSkinPatch.Version);
            byte[] paintedAsset = NativeSkinPatch.BuildAsset(purple, asset), paintedToolbar = NativeToolbarPatch.Create(toolbar, purple);
            File.WriteAllBytes(appPath, plan.Bytes); File.WriteAllBytes(assetPath, paintedAsset); File.WriteAllBytes(toolbarPath, paintedToolbar);
            // The user's exact symptom: old official metadata, missing toolbar
            // metadata, a different draft, but all native files still skinned.
            JsonFile.Write(candidate.StatePath, new PatchState { OriginalAssetHash = NativeSkinPatch.OriginalAssetHash, CurrentAssetHash = NativeSkinPatch.OriginalAssetHash });
            string saved = Path.Combine(fixture, "current.wtskin.json"); ThemeStore.Save(SkinTheme.Presets()[1], saved);
            NativeSkinInspection recovered = NativeSkinService.InspectStores(candidate, statusBar, saved);
            Require(recovered.IsConsistent && recovered.Theme.Name == purple.Name, "official metadata and missing toolbar record did not recover purple");
            Require(NativeSkinPatch.Hash(File.ReadAllBytes(appPath)) == plan.Hash && NativeSkinPatch.Hash(File.ReadAllBytes(assetPath)) == NativeSkinPatch.Hash(paintedAsset)
                && NativeToolbarPatch.Hash(File.ReadAllBytes(toolbarPath)) == NativeToolbarPatch.Hash(paintedToolbar), "recovery changed native files");
            var reopened = NativeSkinService.InspectStores(new NativePatchStore(appPath, assetPath, Path.Combine(fixture, "backups")),
                new NativeToolbarStore(toolbarPath, Path.Combine(fixture, "backups")), saved);
            Require(reopened.IsConsistent && reopened.Theme.Name == purple.Name, "reopening lost recovered state");
            Require(File.Exists(Path.Combine(fixture, "backups", "state.before-recovery.json")), "old metadata was not preserved");
            string recoveredRecordHash = NativeSkinPatch.Hash(File.ReadAllBytes(candidate.StatePath));
            File.WriteAllBytes(assetPath, new byte[] {1, 2, 3, 4});
            bool rejected = false;
            try { NativeSkinService.InspectStores(candidate, statusBar, saved); } catch (InvalidDataException) { rejected = true; }
            Require(rejected && NativeSkinPatch.Hash(File.ReadAllBytes(candidate.StatePath)) == recoveredRecordHash, "unknown asset was accepted or metadata changed");
            File.WriteAllBytes(assetPath, paintedAsset);
            byte[] foreignToolbar = (byte[])paintedToolbar.Clone(); foreignToolbar[foreignToolbar.Length - 1] ^= 1;
            File.WriteAllBytes(toolbarPath, foreignToolbar);
            rejected = false;
            try { NativeSkinService.InspectStores(candidate, statusBar, saved); } catch (InvalidDataException) { rejected = true; }
            Require(rejected && NativeSkinPatch.Hash(File.ReadAllBytes(candidate.StatePath)) == recoveredRecordHash, "unknown toolbar was accepted or metadata changed");
            File.WriteAllText(Path.Combine(output, "summary.txt"), "PASS: 6 targeted checks: stale/missing metadata, different draft, exact native hashes, reopen, metadata backup, reject foreign files.\r\n");
        }
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }
    }
}
