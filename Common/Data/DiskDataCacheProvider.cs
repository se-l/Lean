/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

using System.IO;
using Ionic.Zip;
using System.Linq;
using QuantConnect.Util;
using QuantConnect.Logging;
using QuantConnect.Interfaces;
using System.Collections.Generic;
using static QuantConnect.Util.LeanData;
using System;

namespace QuantConnect.Data
{
    /// <summary>
    /// Simple data cache provider, writes and reads directly from disk
    /// Used as default for <see cref="LeanDataWriter"/>
    /// </summary>
    public class DiskDataCacheProvider : IDataCacheProvider
    {
        private readonly KeyStringSynchronizer _synchronizer;

        /// <summary>
        /// Property indicating the data is temporary in nature and should not be cached.
        /// </summary>
        public bool IsDataEphemeral => false;

        /// <summary>
        /// Creates a new instance
        /// </summary>
        public DiskDataCacheProvider() : this(new KeyStringSynchronizer())
        {
        }

        /// <summary>
        /// Creates a new instance using the given synchronizer
        /// </summary>
        /// <param name="locker">The synchronizer instance to use</param>
        public DiskDataCacheProvider(KeyStringSynchronizer locker)
        {
            _synchronizer = locker;
        }

        /// <summary>
        /// Fetch data from the cache
        /// </summary>
        /// <param name="key">A string representing the key of the cached data</param>
        /// <returns>An <see cref="Stream"/> of the cached data</returns>
        public Stream Fetch(string key)
        {
            LeanData.ParseKey(key, out var filePath, out var entryName);

            return _synchronizer.Execute(filePath, () =>
            {
                if (!File.Exists(filePath))
                {
                    return null;
                }

                try
                {
                    using (var zip = ZipFile.Read(filePath))
                    {
                        ZipEntry entry;
                        if (entryName.IsNullOrEmpty())
                        {
                            // Return the first entry
                            entry = zip[0];
                        }
                        else
                        {
                            // Attempt to find our specific entry
                            if (!zip.ContainsEntry(entryName))
                            {
                                return null;
                            }

                            entry = zip[entryName];
                        }

                        // Extract our entry and return it
                        var stream = new MemoryStream();
                        entry.Extract(stream);
                        stream.Position = 0;
                        return stream;
                    }
                }
                catch (ZipException exception)
                {
                    Log.Error("DiskDataCacheProvider.Fetch(): Corrupt file: " + key + " Error: " + exception);
                    return null;
                }
            });
        }

        /// <summary>
        /// Gets the compressed size of the entry in the zip file in bytes
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentException"></exception>
        public long Size(string key)
        {
            LeanData.ParseKey(key, out var filePath, out var entryName);

            return _synchronizer.Execute(filePath, () =>
            {
                if (!File.Exists(filePath))
                {
                    return 0;
                }

                try
                {
                    using var zip = ZipFile.Read(filePath);
                    
                    if (entryName.IsNullOrEmpty())
                    {
                        // Return the first entry
                        throw new ArgumentException("Entry name is required to get size of the entry");
                    }
                    else
                    {
                        // Attempt to find our specific entry
                        if (!zip.ContainsEntry(entryName))
                        {
                            return 0;
                        }

                        return zip[entryName].CompressedSize;
                    }
                }
                catch (ZipException exception)
                {
                    Log.Error("DiskDataCacheProvider.Fetch(): Corrupt file: " + key + " Error: " + exception);
                    return 0;
                }
            });
        }

        public DateTime? LastModified(string key)
        {
            LeanData.ParseKey(key, out var filePath, out var entryName);

            return _synchronizer.Execute(filePath, DateTime? () =>
            {
                if (!File.Exists(filePath))
                {
                    return null;
                }

                try
                {
                    using var zip = ZipFile.Read(filePath);

                    if (entryName.IsNullOrEmpty())
                    {
                        return null;
                    }
                    else
                    {
                        // Attempt to find our specific entry
                        if (!zip.ContainsEntry(entryName))
                        {
                            return null;
                        }

                        return zip[entryName].LastModified;
                    }
                }
                catch (ZipException exception)
                {
                    Log.Error("DiskDataCacheProvider.Fetch(): Corrupt file: " + key + " Error: " + exception);
                    return null;
                }
            });
        }

        /// <summary>
        /// Store the data in the cache. Not implemented in this instance of the IDataCacheProvider
        /// </summary>
        /// <param name="key">The source of the data, used as a key to retrieve data in the cache</param>
        /// <param name="data">The data as a byte array</param>
        public void Store(string key, byte[] data)
        {
            LeanData.ParseKey(key, out var filePath, out var entryName);

            _synchronizer.Execute(filePath, singleExecution: false, () =>
            {
                Compression.ZipCreateAppendData(filePath, entryName, data, true);
            });
        }

        /// <summary>
        /// Store the data in the cache. 
        /// </summary>
        public void Store(IEnumerable<FileMember> entries, bool overrideEntry = false)
        {
            foreach (var group in entries.GroupBy(entry => entry.FilePath))
            {
                var filePath = group.Key;
                _synchronizer.Execute(filePath, singleExecution: false, () =>
                {
                    // If a destination file can't be read as a zip (e.g. it exists on the mount but is
                    // empty/partial/invalid yet), start from a fresh archive rather than throwing an invalid-zip
                    // error. On networked/S3-backed mounts a write can be visible to File.Exists before its
                    // bytes are fully synced, so this must be tolerated, not fatal.
                    using var zip = TryReadZip(filePath) ?? new ZipFile();
                    foreach (var entry in group)
                    {
                        if (zip.ContainsEntry(entry.EntryName) && overrideEntry)
                        {
                            zip.RemoveEntry(entry.EntryName);
                            Log.Trace($"DiskDataCacheProvider.Store(): Override csv member: {filePath} @ {entry.EntryName}");
                            zip.AddEntry(entry.EntryName, entry.Data);
                        }
                        else if (zip.ContainsEntry(entry.EntryName))
                        {
                            // Keep existing entry unless overriding
                            continue;
                        }
                        else
                        {
                            Log.Trace($"DiskDataCacheProvider.Store(): Create csv member: {filePath} @ {entry.EntryName}");
                            zip.AddEntry(entry.EntryName, entry.Data);
                        }
                    }

                    zip.UseZip64WhenSaving = Zip64Option.Always;

                    // Write to a temp file and atomically move it into place so a concurrent reader
                    // (this downloader's own checks, or another process on the same mount) never observes
                    // a partially-written zip.
                    var tempFile = filePath + ".tmp";
                    zip.Save(tempFile);
                    File.Move(tempFile, filePath, overwrite: true);
                });
            }
        }

        /// <summary>
        /// Attempts to open an existing file as a zip. Returns null when the file does not exist
        /// or cannot currently be read as a valid zip, so callers can fall back to a fresh archive.
        /// </summary>
        private static ZipFile TryReadZip(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            try
            {
                return ZipFile.Read(filePath);
            }
            catch (Exception exception) when (exception is ZipException || exception is IOException)
            {
                Log.Debug($"DiskDataCacheProvider.Store(): Unable to read existing zip, starting fresh: {filePath} {exception.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns a list of zip entries in a provided zip file
        /// </summary>
        public List<string> GetZipEntries(string zipFile)
        {
            return _synchronizer.Execute(zipFile, () =>
            {
                try
                {
                    using var stream = new FileStream(FileExtension.ToNormalizedPath(zipFile), FileMode.Open, FileAccess.Read);
                    // A placeholder/fresh file that exists on disk but has not been written as a
                    // valid zip yet (e.g. an empty or interrupted write) is not readable as a zip.
                    // Treat that as "no entries" so it isn't mistaken for a corrupt file. This is a
                    // plain local-filesystem case; it is not specific to S3/networked mounts.
                    if (stream.Length == 0)
                    {
                        return new List<string>();
                    }
                    return Compression.GetZipEntryFileNames(stream).ToList();
                }
                catch (Exception exception) when (exception is ZipException || exception is IOException)
                {
                    Log.Debug($"DiskDataCacheProvider.GetZipEntries(): Unable to read zip, treating as no entries: {zipFile} {exception.Message}");
                    return new List<string>();
                }
            });
        }

        /// <summary>
        /// Dispose for this class
        /// </summary>
        public void Dispose()
        {
            //NOP
        }
    }
}
