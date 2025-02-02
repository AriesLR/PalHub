﻿using Aetherium.Components.Functions.Config;
using Aetherium.Components.Functions.Services;
using Alphaleonis.Win32.Vss;
using System.Diagnostics;
using System.IO.Compression;

namespace Aetherium.Components.Functions.Server
{
    public static class Backup
    {
        // Start the backup, I kinda understand this code. However the methods it uses are wizardry and therefore good luck with this, I can't help you.
        public static void PerformBackup()
        {
            string sourceDir = Configuration.Instance.SavePath;
            if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            {
                Debug.WriteLine("SavePath is not set or does not exist. Backup process aborted.");
                ToastService.Alert("SavePath is not set or does not exist.\n\nBackup process aborted.\n\nCheck your configuration.");
                return;
            }

            // Make a temp folder and put the backup there first.
            string tempBackupFolder = Path.Combine(Configuration.Instance.BackupPath, "Aetherium_Backup_" + Guid.NewGuid().ToString());

            // Set the backupRoot for the magic AlphaVSS code.
            string backupRoot = tempBackupFolder;

            // Somewhat understandable magic
            using (VssBackup vss = new VssBackup())
            {
                try
                {
                    vss.Setup(Path.GetPathRoot(sourceDir));
                    string snapDirPath = vss.GetSnapshotPath(sourceDir);

                    CopyDirectory(snapDirPath, backupRoot);

                    // Compress the backup folder
                    string zipFileName = $"{Configuration.Instance.ConfigName}_backup_{DateTime.Now:MM-dd-yyyy-HHmm-ss}.zip";
                    string zipFilePath = Path.Combine(Configuration.Instance.BackupPath, zipFileName);
                    ZipFile.CreateFromDirectory(tempBackupFolder, zipFilePath);

                    // Remove the temporary backup folder
                    Directory.Delete(tempBackupFolder, true);

                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"An error occurred during the backup process: {ex.Message}");
                    ToastService.Alert($"An error occurred during the backup process:\n{ex.Message}");
                }
            }
        }

        // Can you read the method name? That's what it does.
        private static void CopyDirectory(string sourceDirName, string destDirName)
        {
            DirectoryInfo dir = new DirectoryInfo(sourceDirName);

            if (!dir.Exists)
            {
                throw new DirectoryNotFoundException("Source directory does not exist or could not be found: " + sourceDirName);
            }

            DirectoryInfo[] dirs = dir.GetDirectories();
            if (!Directory.Exists(destDirName))
            {
                Directory.CreateDirectory(destDirName);
            }

            FileInfo[] files = dir.GetFiles();
            foreach (FileInfo file in files)
            {
                string tempPath = Path.Combine(destDirName, file.Name);
                file.CopyTo(tempPath, false);
            }

            foreach (DirectoryInfo subdir in dirs)
            {
                string tempPath = Path.Combine(destDirName, subdir.Name);
                CopyDirectory(subdir.FullName, tempPath);
            }
        }

        // Anything below this point is *Magic* Refer to AlphaVSS docs or just cry, I prefer to cry.
        public class VssBackup : IDisposable
        {
            bool ComponentMode = false;
            IVssBackupComponents _backup;
            Snapshot _snap;

            public VssBackup()
            {
                InitializeBackup();
            }

            public void Setup(string volumeName)
            {
                Discovery(volumeName);
                PreBackup();
            }

            public void Dispose()
            {
                try { Complete(true); } catch { }

                if (_snap != null)
                {
                    _snap.Dispose();
                    _snap = null;
                }

                if (_backup != null)
                {
                    _backup.Dispose();
                    _backup = null;
                }
            }

            void InitializeBackup()
            {
                IVssFactory vss = VssFactoryProvider.Default.GetVssFactory();
                _backup = vss.CreateVssBackupComponents();
                _backup.InitializeForBackup(null);
                _backup.GatherWriterMetadata();
            }

            void Discovery(string fullPath)
            {
                if (ComponentMode)
                    ExamineComponents(fullPath);
                else
                    _backup.FreeWriterMetadata();

                _snap = new Snapshot(_backup);
                _snap.AddVolume(Path.GetPathRoot(fullPath));
            }


            void ExamineComponents(string fullPath)
            {
                IList<IVssExamineWriterMetadata> writer_mds = _backup.WriterMetadata;

                foreach (IVssExamineWriterMetadata metadata in writer_mds)
                {
                    Trace.WriteLine("Examining metadata for " + metadata.WriterName);

                    foreach (IVssWMComponent cmp in metadata.Components)
                    {
                        Trace.WriteLine("  Component: " + cmp.ComponentName);
                        Trace.WriteLine("  Component info: " + cmp.Caption);

                        foreach (VssWMFileDescriptor file in cmp.Files)
                        {

                            Trace.WriteLine("    Path: " + file.Path);
                            Trace.WriteLine("       Spec: " + file.FileSpecification);

                        }
                    }
                }
            }

            void PreBackup()
            {
                Debug.Assert(_snap != null);

                _backup.SetBackupState(ComponentMode,
                      true, VssBackupType.Full, false);
                _backup.PrepareForBackup();

                _snap.Copy();
            }

            public string GetSnapshotPath(string localPath)
            {
                Trace.WriteLine("New volume: " + _snap.Root);

                if (Path.IsPathRooted(localPath))
                {
                    string root = Path.GetPathRoot(localPath);
                    localPath = localPath.Replace(root, String.Empty);
                }
                string slash = Path.DirectorySeparatorChar.ToString();
                if (!_snap.Root.EndsWith(slash) && !localPath.StartsWith(slash))
                    localPath = localPath.Insert(0, slash);
                localPath = localPath.Insert(0, _snap.Root);

                Trace.WriteLine("Converted path: " + localPath);

                return localPath;
            }


            public System.IO.Stream GetStream(string localPath)
            {
                return File.OpenRead(GetSnapshotPath(localPath));
            }

            void Complete(bool succeeded)
            {
                if (ComponentMode)
                {
                    IList<IVssExamineWriterMetadata> writers = _backup.WriterMetadata;
                    foreach (IVssExamineWriterMetadata metadata in writers)
                    {
                        foreach (IVssWMComponent component in metadata.Components)
                        {
                            _backup.SetBackupSucceeded(
                                  metadata.InstanceId, metadata.WriterId,
                                  component.Type, component.LogicalPath,
                                  component.ComponentName, succeeded);
                        }
                    }

                    _backup.FreeWriterMetadata();
                }

                try
                {
                    _backup.BackupComplete();
                    Debug.WriteLine("[DEBUG]: Backup Complete");
                }
                catch (VssBadStateException) { }
            }

            string FileToPathSpecification(VssWMFileDescriptor file)
            {
                string path = Environment.ExpandEnvironmentVariables(file.Path);

                if (!String.IsNullOrEmpty(file.AlternateLocation))
                    path = Environment.ExpandEnvironmentVariables(
                          file.AlternateLocation);

                string spec = file.FileSpecification.Replace("*.*", "*");


                return Path.Combine(path, file.FileSpecification);
            }
        }

        class Snapshot : IDisposable
        {
            IVssBackupComponents _backup;

            VssSnapshotProperties _props;

            Guid _set_id;

            Guid _snap_id;


            public Snapshot(IVssBackupComponents backup)
            {
                _backup = backup;
                _set_id = backup.StartSnapshotSet();
            }

            public void Dispose()
            {
                try { Delete(); } catch { }
            }

            public void AddVolume(string volumeName)
            {
                if (_backup.IsVolumeSupported(volumeName))
                    _snap_id = _backup.AddToSnapshotSet(volumeName);
                else
                    throw new VssVolumeNotSupportedException(volumeName);
            }

            public void Copy()
            {
                _backup.DoSnapshotSet();
            }

            public void Delete()
            {
                _backup.DeleteSnapshotSet(_set_id, false);
            }

            public string Root
            {
                get
                {
                    if (_props == null)
                        _props = _backup.GetSnapshotProperties(_snap_id);
                    return _props.SnapshotDeviceObject;
                }
            }
        }
    }
}
