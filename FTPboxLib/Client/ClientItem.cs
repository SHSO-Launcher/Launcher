﻿/* License
 * This file is part of FTPbox - Copyright (C) 2012-2013 ftpbox.org
 * FTPbox is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published 
 * by the Free Software Foundation, either version 3 of the License, or (at your option) any later version. This program is distributed 
 * in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. 
 * See the GNU General Public License for more details. You should have received a copy of the GNU General Public License along with this program. 
 * If not, see <http://www.gnu.org/licenses/>.
 */
/* ClientItem.cs
 * Represents an item (file, folder etc) that is found on the server. Used by the client class.
 */

using System;
using System.IO;

namespace FTPboxLib
{
    public class ClientItem
    {
        public ClientItem(){}

        public ClientItem(string name, string path, ClientItemType type, long size = 0x0, DateTime lastWriteTime = default(DateTime))
        {
            Name = name;
            FullPath = path;
            Type = type;
            Size = size;
            //Console.WriteLine("File=" + Name + "  Size=" + Size);  // CSP
            LastWriteTime = lastWriteTime;
        }

        public ClientItem(FileInfo info)
        {
            Name = info.Name;
            FullPath = info.FullName;
            Type = ClientItemType.File;
            LastWriteTime = info.LastWriteTime;
            try
            {
                Size = info.Length;
            }
            catch
            {
                Size = -1;
            }
        }

        public ClientItem(DirectoryInfo info)
        {
            Name = info.Name;
            Type = ClientItemType.Folder;
            LastWriteTime = DateTime.MinValue;  // Doesn't matter
            Size = 0x0;                         // Doesn't matter
        }

        #region Properties

        public string Name { get; set; }

        public string FullPath { get; set; }

        public string NewFullPath { get; set; }

        public ClientItemType Type { get; set; }

        public long Size { get; set; }

        public DateTime CreationTime = default(DateTime);

        public DateTime LastWriteTime { get; set; }

        public string Permissions { get; set; }

        #endregion
    }
}
