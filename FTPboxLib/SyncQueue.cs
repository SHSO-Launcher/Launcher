﻿/* License
 * This file is part of FTPbox - Copyright (C) 2012-2013 ftpbox.org
 * FTPbox is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published 
 * by the Free Software Foundation, either version 3 of the License, or (at your option) any later version. This program is distributed 
 * in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. 
 * See the GNU General Public License for more details. You should have received a copy of the GNU General Public License along with this program. 
 * If not, see <http://www.gnu.org/licenses/>.
 */
/* SyncQueue.cs
 * A queue of items to be synchronized. 
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FTPboxLib
{
    public class SyncQueue : List<SyncQueueItem>
    {
        private Dictionary<SyncQueueItem, StatusType> _completedList = new Dictionary<SyncQueueItem, StatusType>();

        private readonly AccountController _controller;

        int totalCnt = 0;   // CSP
        public static long totalSize = 0; // CSP
        public static long currSize = 0; // CSP
        public static Label totalSizeLabel = null;  // CSP
        public static Label label2 = null;  // CSP
        public static ProgressBar progressBar1 = null; // CSP
        public static Button button1 = null; // CSP


        public SyncQueue(AccountController account)
        {
            _controller = account;
            account.WebInterface.InterfaceRemoved += async (o, e) =>
            {
                if (account.Account.SyncMethod == SyncMethod.Automatic)
                    await ScheduleAutoSync();
                Running = false;
            };
            account.WebInterface.InterfaceUploaded += async (o, e) =>
            {
                if (account.Account.SyncMethod == SyncMethod.Automatic)
                    await ScheduleAutoSync();
                Running = false;
            };
        }

        #region Methods : Handle the Queue List

        /// <summary>
        /// Adds the new item to the Sync Queue 
        /// Also checks for any items in the queue that refer
        /// to the same file/folder and updates them accordingly
        /// </summary>
        /// <param name="item"></param>
        public new async Task Add(SyncQueueItem item)
        {
            Log.Write(l.Client, "adding to list: {0} lwt: {1}", item.CommonPath, item.Item.LastWriteTime);

            if (item.Item.Type == ClientItemType.Folder && item.SyncTo == SyncTo.Remote
                && item.ActionType != ChangeAction.deleted && item.ActionType != ChangeAction.renamed)
            {
                await CheckLocalFolder(item);
            }
            else
            {
                var oldItem = this.FirstOrDefault(x => x.NewCommonPath == item.CommonPath);
                if (oldItem != default(SyncQueueItem))
                {
                    if (item.ActionType == ChangeAction.deleted)
                    {
                        if (oldItem.ActionType == ChangeAction.renamed)
                        {
                            base[IndexOf(oldItem)].ActionType = ChangeAction.deleted;
                            base[IndexOf(oldItem)].SkipNotification = true;
                        }
                        else
                            Remove(oldItem);
                    }
                    else if (item.ActionType == ChangeAction.renamed)
                    {
                        if (oldItem.ActionType == ChangeAction.renamed)
                            base[IndexOf(oldItem)].Item.NewFullPath = item.Item.NewFullPath;
                    }
                    else
                    {
                        if (oldItem.ActionType == ChangeAction.renamed)
                        {
                            base[IndexOf(oldItem)].ActionType = ChangeAction.deleted;
                            base[IndexOf(oldItem)].AddedOn = DateTime.Now;
                        }
                        else
                            Remove(oldItem);
                    }
                }
                if (item.ActionType == ChangeAction.renamed)
                {
                    var existing = this.FirstOrDefault(x => x.CommonPath == item.CommonPath);
                    if (existing != default(SyncQueueItem))
                    {
                        if (existing.ActionType == ChangeAction.changed || existing.ActionType == ChangeAction.created)
                        {
                            base[IndexOf(existing)].ActionType = ChangeAction.deleted;
                            item.ActionType = ChangeAction.created;
                            item.Item.FullPath = item.Item.NewFullPath;

                            return;
                        }
                    }
                }

                item.AddedOn = DateTime.Now;
                base.Add(item);
            }

            // Start syncing from the queue
            if (!Running)
                await StartQueue();
            else
                Log.Write(l.Error, "Queue already running");
        }

        public async Task StartQueue()
        {
            var isAutoCheck = this.Count == 1
                && !autoSyncCancelation.IsCancellationRequested
                && this.ElementAt(0).Item.FullPath == ".";

            if (!isAutoCheck)
                Notifications.ChangeTrayText(MessageType.Syncing);

            CancelAutoSync();

            Running = true;

            while (this.Count > 0)
            {
                var item = this.First();
                var status = StatusType.Waiting;
                RemoveAt(0);

                if ((_controller.Account.SyncDirection == SyncDirection.Local && item.SyncTo == SyncTo.Remote) ||
                    (_controller.Account.SyncDirection == SyncDirection.Remote && item.SyncTo == SyncTo.Local))
                {
                    item.SkipNotification = true;
                    status = StatusType.Skipped;
                }
                else
                {
                    // do stuff here
                    switch (item.ActionType)
                    {
                        case ChangeAction.deleted:
                            status = await DeleteItem(item);
                            break;
                        case ChangeAction.renamed:
                            status = await RenameItem(item);
                            break;
                        case ChangeAction.changed:
                        case ChangeAction.created:
                            status = await CheckUpdateItem(item);
                            break;
                    }
                }
                item.Status = status;
                _completedList.Add(item, status);

                RemoveLast(item);

                if (this.Count == 0) await Task.Delay(1000);
            }

            Running = false;

            Settings.SaveProfile();

            Notifications.ChangeTrayText(MessageType.AllSynced);

            // Notifications time

            var successful = _completedList.Where(x => x.Value == StatusType.Success && !x.Key.SkipNotification).Select(x => x.Key);
            var failed = _completedList.Values.Count(status => status == StatusType.Failure);
            var folders = successful.Count(x => x.Item.Type == ClientItemType.Folder);
            var files = successful.Count(x => x.Item.Type == ClientItemType.File);

            Log.Write(l.Info, "###############################");
            Log.Write(l.Info, "{0} files successfully synced", files);
            Log.Write(l.Info, "{0} folders successfully synced", folders);
            Log.Write(l.Info, "{0} failed to sync", failed);
            Log.Write(l.Info, "###############################");

            if (folders > 0 && files > 0)
            {
                Notifications.Show(files, folders);
            }
            else if ((folders == 1 && files == 0) || (folders == 0 && files == 1))
            {
                var lastItem = files == 1
                    ? successful.Last(x => x.Item.Type == ClientItemType.File)
                    : successful.Last(x => x.Item.Type == ClientItemType.Folder);
                if (lastItem.ActionType == ChangeAction.renamed)
                    Notifications.Show(Common._name(lastItem.CommonPath), ChangeAction.renamed,
                        Common._name(lastItem.NewCommonPath));
                else
                    Notifications.Show(lastItem.Item.Name, lastItem.ActionType, files == 1);
            }
            else if (!(files == 0 && folders == 0))
            {
                var count = (folders == 0) ? files : folders;
                Notifications.Show(count, folders == 0);
            }

            // print completed list
            const string frmt = "{0, -9}{1, -9}{2, -9}{3, -10}{4}";
            Log.Write(l.Info, string.Format(frmt, "Added On", "Action", "SyncTo", "Status", "Common Path"));

            foreach (var i in _completedList.Keys.OrderBy(x => x.AddedOn))
                Log.Write(l.Info, string.Format(frmt, i.AddedOn.FormatDate(), i.ActionType, i.SyncTo, i.Status, i.CommonPath));

            _completedList.Clear();

            _controller.LoadLocalFolders();

            _controller.Client.SetKeepAlive();

            // Check for any pending WebUI actions
            if (_controller.WebInterface.DeletePending || _controller.WebInterface.UpdatePending)
                await _controller.WebInterface.Update();
            else
            {
                if (_controller.Account.SyncMethod == SyncMethod.Automatic)
                    await ScheduleAutoSync();
                Running = false;
            }
        }

        /// <summary>
        /// Moves the last item from the queue to the CompletedList and adds it to FileLog
        /// </summary>
        /// <param name="item"></param>
        public void RemoveLast(SyncQueueItem item)
        {
            item.CompletedOn = DateTime.Now;

            // Add last item to FileLog
            if (item.Status == StatusType.Success)
            {
                if (item.Item.Type == ClientItemType.Folder)
                {
                    switch (item.ActionType)
                    {
                        case ChangeAction.deleted:
                            _controller.FileLog.RemoveFolder(item.CommonPath);
                            break;
                        case ChangeAction.renamed:
                            _controller.FileLog.PutFolder(item.NewCommonPath, item.CommonPath);
                            break;
                        default:
                            _controller.FileLog.PutFolder(item.CommonPath);
                            break;
                    }
                }
                else if (item.Item.Type == ClientItemType.File)
                {
                    switch (item.ActionType)
                    {
                        case ChangeAction.deleted:
                            _controller.RemoveFromLog(item.CommonPath);
                            break;
                        case ChangeAction.renamed:
                            _controller.RemoveFromLog(item.CommonPath);
                            _controller.FileLog.PutFile(item);
                            break;
                        default:
                            _controller.FileLog.PutFile(item);
                            break;
                    }
                }
            }
        }

        public async Task CheckRemoteToLocal()
        {
            var syncItem = new SyncQueueItem(_controller)
            {
                Item = new ClientItem(".", ".", ClientItemType.Folder),
                ActionType = ChangeAction.changed,
                SyncTo = SyncTo.Local,
                SkipNotification = true
            };
            await Add(syncItem);
        }

        /// <summary>
        /// Used in automatic-syncing mode. Will set a timer to check the remote folder for changes
        /// every x seconds ( where x is the user-specified Profile.SyncFrequency in seconds)
        /// </summary>
        public async Task ScheduleAutoSync()
        {
            autoSyncCancelation = new CancellationTokenSource();
            var delay = TimeSpan.FromSeconds(_controller.Account.SyncFrequency);

            Log.Write(l.Debug, $"Scheduling auto sync for {DateTime.Now.Add(delay)}");

            try
            {
                await Task.Delay(delay, autoSyncCancelation.Token);
                Log.Write(l.Debug, $"Starting scheduled auto sync {DateTime.Now}");
                // no cancel, proceed to sync remote-to-local
                if (_controller.Account.SyncMethod == SyncMethod.Automatic)
                    await CheckRemoteToLocal();
            }
            catch (TaskCanceledException)
            {
                Log.Write(l.Info, "Scheduled auto sync was canceled");
            }
        }

        CancellationTokenSource autoSyncCancelation = new CancellationTokenSource();
        public void CancelAutoSync()
        {
            autoSyncCancelation?.Cancel();
        }

        #endregion

        #region Private Methods : Dealing with a single item of the queue

        /// <summary>
        /// Check a local folder and all of its subitems for changes
        /// </summary>
        private async Task CheckLocalFolder(SyncQueueItem folder)
        {
            if (_controller.ItemSkipped(folder.Item) && folder.CommonPath != ".") return;

            var cp = (folder.Item.FullPath == _controller.Paths.Local) ? "." : folder.CommonPath;

            var cpExists = cp == "." || _controller.Client.Exists(cp);

            if (!cpExists)
            {
                folder.AddedOn = DateTime.Now;
                base.Add(folder);
            }

            var remoteFilesList = cpExists
                ? (await _controller.Client.ListRecursive(cp)).Select(x => x.FullPath).ToList()
                : new List<string>();

            remoteFilesList = remoteFilesList.ConvertAll(x => _controller.GetCommonPath(x, false));

            if (_controller.Client.ListingFailed)
            {
                folder.Status = StatusType.Failure;
                folder.CompletedOn = DateTime.Now;
                _completedList.Add(folder, StatusType.Failure);
                await _controller.Client.Reconnect();
                return;
            }

            var di = new DirectoryInfo(folder.LocalPath);
            foreach (var d in di.GetDirectories("*", SearchOption.AllDirectories))
            {
                var cpath = _controller.GetCommonPath(d.FullName, true);

                if (remoteFilesList.Contains(cpath)) continue;
                if (_controller.ItemSkipped(d)) continue;

                base.Add(new SyncQueueItem(_controller)
                {
                    Item = new ClientItem(d) { FullPath = d.FullName },
                    ActionType = ChangeAction.changed,
                    Status = StatusType.Waiting,
                    SyncTo = SyncTo.Remote
                });
            }

            foreach (var f in di.GetFiles("*", SearchOption.AllDirectories))
            {
                if (_controller.ItemSkipped(f)) continue;

                var sqi = new SyncQueueItem(_controller)
                {
                    Item = new ClientItem(f),
                    ActionType = ChangeAction.changed,
                    Status = StatusType.Waiting,
                    SyncTo = SyncTo.Remote
                };
                var cpath = sqi.CommonPath;

                if (!_controller.FileLog.Contains(cpath) && remoteFilesList.Contains(cpath))
                {
                    // untracked file, exists both locally and on server.
                    var areIdentical = await _controller.Client.FilesAreIdentical(cpath, f.FullName);
                    if (areIdentical)
                    {
                        _controller.FileLog.PutFile(sqi);
                        continue;
                    }
                }
                else if (remoteFilesList.Contains(cpath) && _controller.FileLog.GetLocal(cpath) == f.LastWriteTime)
                {
                    // tracked file, local file unmodified.
                    continue;
                }
                // local file should be uploaded.
                base.Add(sqi);
            }
        }

        /// <summary>
        /// Delete the specified item (folder or file)
        /// </summary>
        private async Task<StatusType> DeleteItem(SyncQueueItem item)
        {
            try
            {
                if (item.SyncTo == SyncTo.Local)
                {
                    _controller.FolderWatcher.Pause();   // Pause watchers
                    if (item.Item.Type == ClientItemType.File)
                    {
                        Common.RecycleOrDeleteFile(item.LocalPath);
                    }
                    else if (item.Item.Type == ClientItemType.Folder)
                    {
                        Common.RecycleOrDeleteFolder(item.LocalPath);
                    }
                    _controller.FolderWatcher.Resume();  // Resume watchers
                }
                else
                {
                    if (item.Item.Type == ClientItemType.File)
                    {
                        await _controller.Client.Remove(item.CommonPath);
                    }
                    else if (item.Item.Type == ClientItemType.Folder)
                    {
                        await _controller.Client.RemoveFolder(item.CommonPath);
                    }
                }
                // Success?
                item.Status = StatusType.Success;
                return StatusType.Success;
            }
            catch (Exception ex)
            {
                ex.LogException();
                _controller.FolderWatcher.Resume();      // Resume watchers
                return StatusType.Failure;
            }
        }

        /// <summary>
        /// Rename the specified item (folder or file)
        /// This is only called when a local item is renamed
        /// </summary>
        private async Task<StatusType> RenameItem(SyncQueueItem item)
        {
            try
            {
                Log.Write(l.Client, "Renaming: [{0}] -> [{1}]", item.CommonPath, item.NewCommonPath);
                // Cannot detect remote renaming, atleast not yet
                if (item.SyncTo == SyncTo.Remote)
                    await _controller.Client.Rename(item.CommonPath, item.NewCommonPath);
                // Success?
                return StatusType.Success;
            }
            catch
            {
                if (!_controller.Client.Exists(item.CommonPath) && _controller.Client.Exists(item.NewCommonPath))
                    return StatusType.Success;
                else
                    return StatusType.Failure;
            }
        }

        /// <summary>
        /// Synchronize the specified item with ActionType of changed or created.
        /// If the sync destination is our local folder, check if the item is already up-to-date first.
        /// </summary>
        private async Task<StatusType> CheckUpdateItem(SyncQueueItem item)
        {
            TransferStatus status;
            if (item.Item.Type == ClientItemType.File)
            {
                status = (item.SyncTo == SyncTo.Remote)
                    ? await _controller.Client.SafeUpload(item)
                    : await CheckExistingFile(item);

                if (status == TransferStatus.None)
                    return StatusType.Skipped;
                else
                    return status == TransferStatus.Success ? StatusType.Success : StatusType.Failure;
            }
            if (item.Item.Type == ClientItemType.Folder && item.SyncTo == SyncTo.Remote)
            {
                try
                {
                    await _controller.Client.MakeFolder(item.CommonPath);
                    return StatusType.Success;
                }
                catch
                {
                    return StatusType.Failure;
                }
            }
            // else: Folder, Sync to local
            Notifications.ChangeTrayText(MessageType.Listing);
            var allItems = new List<ClientItem>();
            Log.Write(l.Debug, "Syncing remote folder {0} to local", item.CommonPath);

            if (!_controller.Client.CheckWorkingDirectory())
            {
                return StatusType.Failure;
            }

            var list = await _controller.Client.ListRecursive(item.CommonPath);

            if (_controller.Client.ListingFailed)
            {
                await _controller.Client.Reconnect();
                return StatusType.Failure;
            }

            /////// block added by CSP ///////////////
            foreach (var f in list)
            {
                totalSize += f.Size;
            }
            Console.WriteLine("totalSize = " + totalSize);  // CSP
            if (totalSizeLabel != null)
                totalSizeLabel.Text = "";  // "Total Size: " + (totalSize / 1000000).ToString() + " MB";
            progressBar1.Minimum = 0;
            progressBar1.Maximum = (int)(totalSize / 1000000);
            ///////////////////////////////////////////

            foreach (var f in list)
            {
                allItems.Add(f);

                totalCnt++;  // CSP
                Console.WriteLine("CheckUpdateItem: (" + totalCnt + ") " + f.Name + " " + f.Size);  // CSP
                

                var cpath = _controller.GetCommonPath(f.FullPath, false);
                var lpath = Path.Combine(_controller.Paths.Local, cpath);

                if (_controller.ItemSkipped(f)) continue;

                if (this.Any(x => x.CommonPath == cpath && x.ActionType == ChangeAction.deleted && x.SyncTo == SyncTo.Remote))
                {
                    //TODO: what about files when folder is scheduled for deletion?
                    Log.Write(l.Info, $"Skipping item because it is scheduled for deletion: {cpath}");
                    continue;
                }

                /////////// block added by CSP ////////////////////
                //totalSizeLabel.Text = "Processing file: " + f.Name;
                //currSize += f.Size;
                //progressBar1.Value = (int)(currSize / 1000000);   
                //int percentVal = (int)((progressBar1.Value / progressBar1.Maximum) * 100);
                //label2.Text = progressBar1.Value + " / " + (int)(totalSize / 1000000) + " MB"; //percentVal.ToString() + "%";
                //Console.WriteLine("progress: " + progressBar1.Value + " / " + (int)(totalSize / 1000000) + " MB");
                ///////////////////////////////////////////////////////////

                var sqi = new SyncQueueItem(_controller)
                {
                    Status = StatusType.Success,
                    Item = f,
                    ActionType = ChangeAction.created,
                    AddedOn = DateTime.Now,
                    CompletedOn = DateTime.Now,
                    SyncTo = SyncTo.Local
                };

                if (f.Type == ClientItemType.Folder && !Directory.Exists(lpath))
                {
                    _controller.FolderWatcher.Pause();
                    Directory.CreateDirectory(lpath);
                    _controller.FolderWatcher.Resume();
                    sqi.CompletedOn = DateTime.Now;
                    sqi.Status = StatusType.Success;
                    _completedList.Add(sqi, StatusType.Success);
                    // Add to log
                    _controller.FileLog.PutFolder(sqi.CommonPath);
                }
                else if (f.Type == ClientItemType.File)
                {
                    //////// block commented out by CSP ///////////
                    //status = !File.Exists(lpath) 
                    //    ? await _controller.Client.SafeDownload(sqi) 
                    //    : await CheckExistingFile(sqi);
                    //////////////////////////////////////////////

                    ////////// block by CSP   //////////
                    if (!File.Exists(lpath))
                    {
                        status = await _controller.Client.SafeDownload(sqi);

                        /////////// block added by CSP ////////////////////
                        totalSizeLabel.Text = "New file: " + f.Name;
                        totalSizeLabel.Refresh();
                        currSize += f.Size;
                        progressBar1.Value = (int)(currSize / 1000000);   
                        int percentVal = (int)((progressBar1.Value / progressBar1.Maximum) * 100);
                        label2.Text = progressBar1.Value + " / " + (int)(totalSize / 1000000) + " MB"; //percentVal.ToString() + "%";
                        label2.Refresh();
                        Console.WriteLine("progress: " + progressBar1.Value + " / " + (int)(totalSize / 1000000) + " MB");
                        ///////////////////////////////////////////////////////////
                    }
                    else
                    {
                        status = await CheckExistingFile(sqi);
                    }
                    /////////////////////////////////////


                    if (status == TransferStatus.None) continue;

                    sqi.Status = status == TransferStatus.Success ? StatusType.Success : StatusType.Failure;
                    sqi.CompletedOn = DateTime.Now;
                    _completedList.Add(sqi, sqi.Status);
                    // Add to log
                    if (sqi.Status == StatusType.Success)
                        _controller.FileLog.PutFile(sqi);
                }
            }

            var dInfo = new DirectoryInfo(item.LocalPath);

            // Look for local files that should be deleted
            foreach (var local in dInfo.GetFiles("*", SearchOption.AllDirectories))
            {
                var cpath = _controller.GetCommonPath(local.FullName, true);
                // continue if the file is ignored
                if (_controller.ItemSkipped(local)) continue;
                // continue if the file was found in the remote list
                if (allItems.Any(x => _controller.GetCommonPath(x.FullPath, false) == cpath)) continue;
                // continue if the file is not in the log, or is changed compared to the logged data TODO: Maybe send to remote folder?
                var tbaItem = new SyncQueueItem(_controller)
                {
                    Item = new ClientItem(local),
                    ActionType = ChangeAction.created
                };

                Log.Write(l.Info, $"File was not found on server: {cpath}");
                Log.Write(l.Info, $"Contained in file log: {_controller.FileLog.Contains(cpath)}");

                if (!_controller.FileLog.Contains(cpath) || _controller.FileLog.GetLocal(cpath) != local.LastWriteTime)
                {
                    Log.Write(l.Info, $"The file should be uploaded");
                    // The file has not been uploaded yet
                    tbaItem.Item.FullPath = local.FullName;
                    tbaItem.ActionType = ChangeAction.created;
                    tbaItem.SyncTo = SyncTo.Remote;
                }
                else
                {
                    Log.Write(l.Info, $"The file should be deleted");
                    // Seems like the file was deleted from the remote folder
                    tbaItem.Item.FullPath = cpath;
                    tbaItem.ActionType = ChangeAction.deleted;
                    tbaItem.SyncTo = SyncTo.Local;
                }
                await Add(tbaItem);
            }
            // Look for local folders that should be deleted
            foreach (var local in dInfo.GetDirectories("*", SearchOption.AllDirectories))
            {
                var cpath = _controller.GetCommonPath(local.FullName, true);
                // continue if the folder is ignored
                if (_controller.ItemSkipped(local)) continue;
                // continue if the folder was found in the remote list
                if (allItems.Any(x => _controller.GetCommonPath(x.FullPath, false) == cpath)) continue;
                // continue if the folder is not in the log TODO: Maybe send to remote folder?
                if (!_controller.FileLog.Folders.Contains(cpath)) continue;

                // Seems like the folder was deleted from the remote folder
                await Add(new SyncQueueItem(_controller)
                {
                    Item = new ClientItem(local) { FullPath = _controller.GetCommonPath(local.FullName, true) },
                    ActionType = ChangeAction.deleted,
                    SyncTo = SyncTo.Local
                });
            }

            button1.Enabled = true;  // CSP enable play button when all files are synced.

            return StatusType.Success;
        }

        /// <summary>
        /// Check a single file and find if the remote item is newer than the local one        
        /// </summary>
        private async Task<TransferStatus> CheckExistingFile(SyncQueueItem item)
        {
            var locLwt = File.GetLastWriteTime(item.LocalPath);
            var remLwt = (_controller.Account.Protocol != FtpProtocol.SFTP) ? _controller.Client.TryGetModifiedTime(item.CommonPath) : item.Item.LastWriteTime;

            var locLog = _controller.FileLog.GetLocal(item.CommonPath);
            var remLog = _controller.FileLog.GetRemote(item.CommonPath);

            var rResult = DateTime.Compare(remLwt, remLog);
            var lResult = DateTime.Compare(locLwt, locLog);
            var bResult = DateTime.Compare(remLwt, locLwt);

            var remDif = remLwt - remLog;
            var locDif = locLwt - locLog;

            // Set to TransferStatus.None by default, incase none of the following
            // conditions are met (which means the file is up-to-date already)
            var status = TransferStatus.None;

            var remoteChanged = rResult > 0 && remDif.TotalSeconds > 1;

            var localChanged = lResult > 0 && locDif.TotalSeconds > 1;

            var remoteChangedMore = (remDif.TotalSeconds > locDif.TotalSeconds);

            //  CSP commented out  //if ((remoteChanged && localChanged && remoteChangedMore) || (!localChanged && remoteChanged))
            if (remLwt > locLwt)  // added by CSP
            {
                status = await _controller.Client.SafeDownload(item);

                /////////// block added by CSP ////////////////////
                totalSizeLabel.Text = "Updating file: " + item.Item.Name;
                totalSizeLabel.Refresh();
                currSize += item.Item.Size;
                progressBar1.Value = (int)(currSize / 1000000);   
                int percentVal = (int)((progressBar1.Value / progressBar1.Maximum) * 100);
                label2.Text = progressBar1.Value + " / " + (int)(totalSize / 1000000) + " MB"; //percentVal.ToString() + "%";
                label2.Refresh();
                Console.WriteLine("progress: " + progressBar1.Value + " / " + (int)(totalSize / 1000000) + " MB");
                ///////////////////////////////////////////////////////////
            }
            //////////////////// block commented out by CSP
            //else if (localChanged)
            //{
            //    Log.Write(l.Warning, "{0} seems to have escaped startup check", item.CommonPath);
            //    await Add(new SyncQueueItem(_controller)
            //    {
            //        Item = new ClientItem
            //        {
            //            Name = item.Item.Name,
            //            FullPath = item.LocalPath,
            //            Type = item.Item.Type,
            //            LastWriteTime = File.GetLastWriteTime(item.LocalPath),
            //            Size = new FileInfo(item.LocalPath).Length
            //        },
            //        ActionType = ChangeAction.changed,
            //        Status = StatusType.Waiting,
            //        SyncTo = SyncTo.Remote
            //    });
            //}
            else  // else block added by CSP
            {
                /////////// block added by CSP ////////////////////
                totalSizeLabel.Text = "Skipping file: " + item.Item.Name;
                totalSizeLabel.Refresh();
                currSize += item.Item.Size;
                progressBar1.Value = (int)(currSize / 1000000);
                int percentVal = (int)((progressBar1.Value / progressBar1.Maximum) * 100);
                label2.Text = progressBar1.Value + " / " + (int)(totalSize / 1000000) + " MB"; //percentVal.ToString() + "%";
                label2.Refresh();
                Console.WriteLine("progress: " + progressBar1.Value + " / " + (int)(totalSize / 1000000) + " MB");
                ///////////////////////////////////////////////////////////
            }

            return status;
        }

        #endregion

        public bool Running { get; private set; }
    }
}
