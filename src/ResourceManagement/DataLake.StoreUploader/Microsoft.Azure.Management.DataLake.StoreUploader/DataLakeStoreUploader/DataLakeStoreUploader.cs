﻿// 
// Copyright (c) Microsoft and contributors.  All rights reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//   http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// 
// See the License for the specific language governing permissions and
// limitations under the License.
// 

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Azure.Management.DataLake.StoreUploader
{
    /// <summary>
    /// Represents a general purpose file uploader into DataLake. Supports the efficient upload of large files.
    /// </summary>
    public sealed class DataLakeStoreUploader
    {

        #region Private

        /// <summary>
        /// The maximum number of parallel threads to allow. 
        /// </summary>
        public const int MaxAllowedThreads = 1024;
        private readonly IFrontEndAdapter _frontEnd;
        private readonly IProgress<UploadProgress> _progressTracker;
        private readonly IProgress<UploadFolderProgress> _folderProgressTracker;
        private readonly string _metadataFilePath;
        private readonly CancellationToken _token;
        private int _previousDefaultConnectionLimit;
        private bool isDirectory = false;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new instance of the DataLakeUploader class, by specifying a pointer to the FrontEnd to use for the upload.
        /// </summary>
        /// <param name="uploadParameters">The Upload Parameters to use.</param>
        /// <param name="frontEnd">A pointer to the FrontEnd interface to use for the upload.</param>
        /// <param name="progressTracker">(Optional) A tracker that reports progress on the upload.</param>
        /// <param name="folderProgressTracker">(Optional) The folder progress tracker.</param>
        public DataLakeStoreUploader(UploadParameters uploadParameters, IFrontEndAdapter frontEnd, IProgress<UploadProgress> progressTracker = null, IProgress<UploadFolderProgress> folderProgressTracker = null) :
            this(uploadParameters, frontEnd, CancellationToken.None, progressTracker, folderProgressTracker)
        {
            
        }

        /// <summary>
        /// Creates a new instance of the DataLakeUploader class, by specifying a pointer to the FrontEnd to use for the upload.
        /// </summary>
        /// <param name="uploadParameters">The Upload Parameters to use.</param>
        /// <param name="frontEnd">A pointer to the FrontEnd interface to use for the upload.</param>
        /// <param name="token">The token.</param>
        /// <param name="progressTracker">(Optional) A tracker that reports progress on the upload.</param>
        /// <param name="folderProgressTracker">(Optional) The folder progress tracker.</param>
        public DataLakeStoreUploader(UploadParameters uploadParameters, IFrontEndAdapter frontEnd, CancellationToken token, IProgress<UploadProgress> progressTracker = null, IProgress<UploadFolderProgress> folderProgressTracker = null)
        {
            this.Parameters = uploadParameters;
            _frontEnd = frontEnd;

            //ensure that input parameters are correct
            ValidateParameters();

            _metadataFilePath = GetCanonicalMetadataFilePath();
            _progressTracker = progressTracker;
            _folderProgressTracker = folderProgressTracker;
            _token = token;
        }

        /// <summary>
        /// Gets the canonical metadata file path.
        /// </summary>
        /// <returns></returns>
        private string GetCanonicalMetadataFilePath()
        {
            return Path.Combine(this.Parameters.LocalMetadataLocation, string.Format("{0}.upload.xml", Path.GetFileName(this.Parameters.InputFilePath)));
        }

        #endregion

        #region Invocation

        /// <summary>
        /// Gets the parameters to use for this upload.
        /// </summary>
        public UploadParameters Parameters { get; private set; }

        /// <summary>
        /// Executes the upload as defined by the input parameters.
        /// </summary>
        public void Execute()
        {
            try
            {
                //we need to override the default .NET value for max connections to a host to our number of threads, if necessary (otherwise we won't achieve the parallelism we want)
                _previousDefaultConnectionLimit = ServicePointManager.DefaultConnectionLimit;
                ServicePointManager.DefaultConnectionLimit = Math.Max(this.Parameters.FileThreadCount,
                    ServicePointManager.DefaultConnectionLimit);
                // check if we are uploading a file or a directory
                if (!isDirectory)
                {
                    //load up existing metadata or create a fresh one
                    var metadata = GetMetadata();

                    //match up the metadata with the information on the server
                    if (this.Parameters.IsResume)
                    {
                        ValidateMetadataForResume(metadata);
                    }
                    else
                    {
                        ValidateMetadataForFreshUpload(metadata);
                    }

                    //begin (or resume) uploading the file
                    UploadFile(metadata);

                    //clean up metadata after a successful upload
                    metadata.DeleteFile();
                }
                else
                {
                    var metadata = GetFolderMetadata();
                    
                    //match up the metadata with the information on the server
                    if (this.Parameters.IsResume)
                    {
                        ValidateFolderMetadataForResume(metadata);
                    }
                    else
                    {
                        ValidateFolderMetadataForFreshUpload(metadata);
                    }
                    var folderOptions = new ParallelOptions
                    {
                        CancellationToken = _token,
                        MaxDegreeOfParallelism = this.Parameters.FolderThreadCount
                    };
                    try
                    {
                        var fileProgressTracker = CreateFileProgressTracker(metadata);
                        Parallel.ForEach(metadata.Files, folderOptions,
                            file =>
                            {
                                _token.ThrowIfCancellationRequested();
                                // only initiate uploads for files that are not already complete
                                if (file.Status != SegmentUploadStatus.Complete)
                                {
                                    var segmentProgressTracker = CreateSegmentProgressTracker(file, fileProgressTracker);
                                    UploadFile(file, segmentProgressTracker);
                                }

                                // attempt to save the metadata
                                try
                                {
                                    metadata.Save();
                                    // delete the temp metadata created if it exists.
                                    file.DeleteFile();
                                }
                                catch { } // if we can't save the metadata or delete a temp file we shouldn't fail out.

                                
                            }
                        );

                        metadata.DeleteFile();
                    }
                    catch
                    {
                        try
                        {
                            // if anything went wrong, make sure that we attempt to save the current state of the folder metadata
                            metadata.Save();
                        }
                        catch { } // saving the metadata is a best effort, we will not fail out for this reason and we want to ensure the root exception is preserved.
                        throw;
                    }
                }
            }
            finally
            {
                //revert back the default .NET value for max connections to a host to whatever it was before
                ServicePointManager.DefaultConnectionLimit = _previousDefaultConnectionLimit;
            }
        }

        /// <summary>
        /// Validates the parameters.
        /// </summary>
        /// <exception cref="System.IO.FileNotFoundException">Could not find input file</exception>
        /// <exception cref="System.ArgumentNullException">
        /// TargetStreamPath;Null or empty Target Stream Path
        /// or
        /// AccountName;Null or empty Account Name
        /// </exception>
        /// <exception cref="System.ArgumentException">Invalid TargetStreamPath, a stream path should not end with /</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">ThreadCount</exception>
        private void ValidateParameters()
        {
            if (!File.Exists(this.Parameters.InputFilePath) && !Directory.Exists(this.Parameters.InputFilePath))
            {
                throw new FileNotFoundException("Could not find input file or folder", this.Parameters.InputFilePath);
            }

            if (string.IsNullOrWhiteSpace(this.Parameters.TargetStreamPath))
            {
                throw new ArgumentNullException("TargetStreamPath", "Null or empty Target Stream Path");
            }

            if (this.Parameters.TargetStreamPath.EndsWith("/"))
            {
                throw new ArgumentException("Invalid TargetStreamPath, a stream path should not end with /");
            }

            if (string.IsNullOrWhiteSpace(this.Parameters.AccountName))
            {
                throw new ArgumentNullException("AccountName", "Null or empty Account Name");
            }

            if (this.Parameters.FileThreadCount < 1 || this.Parameters.FileThreadCount > MaxAllowedThreads)
            {
                throw new ArgumentOutOfRangeException(string.Format("ThreadCount must be at least 1 and at most {0}", MaxAllowedThreads), "ThreadCount");
            }

            // if the input is a directory, set it
            isDirectory = Directory.Exists(this.Parameters.InputFilePath);
            
        }

        #endregion

        #region Metadata Operations

        /// <summary>
        /// Gets the metadata.
        /// </summary>
        /// <returns></returns>
        private UploadFolderMetadata GetFolderMetadata()
        {
            var metadataGenerator = new UploadFolderMetadataGenerator(this.Parameters);
            if (this.Parameters.IsResume)
            {
                return metadataGenerator.GetExistingMetadata(_metadataFilePath);
            }
            else
            {
                return metadataGenerator.CreateNewMetadata(_metadataFilePath);
            }
        }

        /// <summary>
        /// Gets the metadata.
        /// </summary>
        /// <returns></returns>
        private UploadMetadata GetMetadata()
        {
            var metadataGenerator = new UploadMetadataGenerator(this.Parameters);
            if (this.Parameters.IsResume)
            {
                return metadataGenerator.GetExistingMetadata(_metadataFilePath);
            }
            else
            {
                return metadataGenerator.CreateNewMetadata(_metadataFilePath);
            }
        }

        /// <summary>
        /// Deletes the metadata file from disk.
        /// </summary>
        internal void DeleteMetadataFile()
        {
            if (File.Exists(_metadataFilePath))
            {
                File.Delete(_metadataFilePath);
            }
        }

        #endregion

        #region Uploading Operations

        /// <summary>
        /// Validates that the metadata is valid for a resume operation, and also updates the internal Segment States to match what the Server looks like.
        /// If any changes are made, the metadata will be saved to its canonical location.
        /// </summary>
        /// <param name="metadata"></param>
        private UploadMetadata ValidateMetadataForResume(UploadMetadata metadata, bool isFolderUpload = false)
        {
            ValidateMetadataMatchesLocalFile(metadata, isFolderUpload);
            if(isFolderUpload && metadata.Status == SegmentUploadStatus.Complete)
            {
                // validate that the target stream does exist. If not, set its status back to pending
                var retryCount = 0;
                while (retryCount < SingleSegmentUploader.MaxBufferUploadAttemptCount)
                {
                    _token.ThrowIfCancellationRequested();
                    retryCount++;
                    try
                    {
                        //verify that the stream exists and that the length is as expected
                        if (!_frontEnd.StreamExists(metadata.TargetStreamPath))
                        {
                            // this file was marked as completed, but no target stream exists; it needs to be reuploaded
                            metadata.Status = SegmentUploadStatus.Pending;
                        }
                        else
                        {
                            var remoteLength = _frontEnd.GetStreamLength(metadata.TargetStreamPath);
                            if (remoteLength != metadata.FileLength)
                            {
                                //the target stream has a different length than the input segment, which implies they are inconsistent; it needs to be reuploaded
                                //in this case it is considered safe to delete the file on the server,
                                //since it is in an inconsistent state and we will be re-uploading it anyway
                                _frontEnd.DeleteStream(metadata.TargetStreamPath);
                                metadata.Status = SegmentUploadStatus.Pending;
                            }
                        }

                        break;
                    }
                    catch (Exception e)
                    {
                        _token.ThrowIfCancellationRequested();
                        if (retryCount >= SingleSegmentUploader.MaxBufferUploadAttemptCount)
                        {
                            throw new UploadFailedException(
                                string.Format(
                                    "Cannot validate metadata for stream: {0} in order to resume due to the following exception retrieving file information: {1}",
                                    metadata.TargetStreamPath,
                                    e));
                        }

                        SingleSegmentUploader.WaitForRetry(retryCount, Parameters.UseSegmentBlockBackOffRetryStrategy, _token);
                    }
                }

                return metadata;
            }
            
            //verify that the target stream does not already exist and hasn't completed (in case we don't want to overwrite)
            if (!this.Parameters.IsOverwrite && _frontEnd.StreamExists(metadata.TargetStreamPath))
            {
                throw new InvalidOperationException(string.Format("Stream at path: {0} already exists. Please set overwrite to true to overwrite streams that exist.", metadata.TargetStreamPath));
            }

            //make sure we don't upload part of the file as binary, while the rest is non-binary (that's just asking for trouble)
            if (this.Parameters.IsBinary != metadata.IsBinary)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "Existing metadata was created for a {0}binary file while the current parameters requested a {1}binary upload.", 
                        metadata.IsBinary ? string.Empty : "non-", 
                        this.Parameters.IsBinary ? string.Empty : "non-"));
            }

            //see what files(segments) already exist - update metadata accordingly (only for segments that are missing from server; if it's on the server but not in metadata, reupload)
            foreach (var segment in metadata.Segments)
            {
                if (segment.Status == SegmentUploadStatus.Complete)
                {
                    var retryCount = 0;
                    while (retryCount < SingleSegmentUploader.MaxBufferUploadAttemptCount)
                    {
                        _token.ThrowIfCancellationRequested();
                        retryCount++;
                        try
                        {
                            //verify that the stream exists and that the length is as expected
                            if (!_frontEnd.StreamExists(segment.Path))
                            {
                                // this segment was marked as completed, but no target stream exists; it needs to be reuploaded
                                segment.Status = SegmentUploadStatus.Pending;
                            }
                            else
                            {
                                var remoteLength = _frontEnd.GetStreamLength(segment.Path);
                                if (remoteLength != segment.Length)
                                {
                                    //the target stream has a different length than the input segment, which implies they are inconsistent; it needs to be reuploaded
                                    segment.Status = SegmentUploadStatus.Pending;
                                }
                            }

                            break;
                        }
                        catch (Exception e)
                        {
                            _token.ThrowIfCancellationRequested();
                            if (retryCount >= SingleSegmentUploader.MaxBufferUploadAttemptCount)
                            {
                                throw new UploadFailedException(
                                    string.Format(
                                        "Cannot validate metadata for stream: {0} in order to resume due to the following exception retrieving file information: {1}",
                                        metadata.TargetStreamPath,
                                        e));
                            }

                            SingleSegmentUploader.WaitForRetry(retryCount, Parameters.UseSegmentBlockBackOffRetryStrategy, _token);
                        }
                    }
                }
                else
                {
                    //anything which is not in 'Completed' status needs to be reuploaded
                    segment.Status = SegmentUploadStatus.Pending;
                }
            }

            if (!isFolderUpload)
            {
                metadata.Save();
            }

            return metadata;
        }

        /// <summary>
        /// Verifies that the metadata is valid for a fresh upload.
        /// </summary>
        /// <param name="metadata"></param>
        private void ValidateMetadataForFreshUpload(UploadMetadata metadata)
        {
            ValidateMetadataMatchesLocalFile(metadata);

            //verify that the target stream does not already exist (in case we don't want to overwrite)
            if (!this.Parameters.IsOverwrite && _frontEnd.StreamExists(metadata.TargetStreamPath))
            {
                throw new InvalidOperationException(string.Format("Target Stream: {0} already exists", metadata.TargetStreamPath));
            }
        }

        /// <summary>
        /// Verifies that the metadata is consistent with the local file information.
        /// </summary>
        /// <param name="metadata"></param>
        private void ValidateMetadataMatchesLocalFile(UploadMetadata metadata, bool isFolderUpload = false)
        {
            //verify that it matches against local file (size, name)
            var metadataInputFileInfo = new FileInfo(metadata.InputFilePath);

            if (isFolderUpload)
            {
                if (!metadata.TargetStreamPath.Trim().Contains(this.Parameters.TargetStreamPath.Trim()))
                {
                    throw new InvalidOperationException(string.Format("Metadata points to a different target stream folder in path: {0} than the input parameters: {1}", metadata.TargetStreamPath, this.Parameters.TargetStreamPath));
                }
            }
            else
            {
                if (metadata.TargetStreamPath.Trim() != this.Parameters.TargetStreamPath.Trim())
                {
                    throw new InvalidOperationException("Metadata points to a different target stream than the input parameters");
                }

                var paramInputFileInfo = new FileInfo(this.Parameters.InputFilePath);

                if (!paramInputFileInfo.FullName.Equals(metadataInputFileInfo.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The metadata refers to different file than the one requested");
                }
            }

            if (!metadataInputFileInfo.Exists)
            {
                throw new InvalidOperationException("The metadata refers to a file that does not exist");
            }

            if (metadata.FileLength != metadataInputFileInfo.Length)
            {
                throw new InvalidOperationException("The metadata's file information differs from the actual file");
            }
        }

        /// <summary>
        /// Validates that the metadata is valid for a resume operation, and also updates the internal Segment States to match what the Server looks like.
        /// If any changes are made, the metadata will be saved to its canonical location.
        /// </summary>
        /// <param name="metadata"></param>
        private void ValidateFolderMetadataForResume(UploadFolderMetadata metadata)
        {
            ValidateFolderMetadataMatchesLocalFile(metadata);
            var exceptions = new ConcurrentQueue<Exception>();
            //see what files already exist - update metadata accordingly (only for segments that are missing from server; if it's on the server but not in metadata, reupload)
            Parallel.For(0, metadata.Files.Length, i =>
            {
                try
                {
                    metadata.Files[i] = ValidateMetadataForResume(metadata.Files[i], true);
                }
                catch(Exception e)
                {
                    exceptions.Enqueue(e);
                }
            });

            if(exceptions.Count > 0)
            {
                throw new AggregateException(exceptions);
            }

            metadata.Save();
        }

        /// <summary>
        /// Verifies that the metadata is valid for a fresh upload.
        /// </summary>
        /// <param name="metadata"></param>
        private void ValidateFolderMetadataForFreshUpload(UploadFolderMetadata metadata)
        {
            ValidateFolderMetadataMatchesLocalFile(metadata);
            var exceptions = new ConcurrentQueue<Exception>();
            Parallel.ForEach(metadata.Files, file =>
            {
                //verify that the target stream does not already exist (in case we don't want to overwrite)
                if (!this.Parameters.IsOverwrite && _frontEnd.StreamExists(file.TargetStreamPath))
                {
                    exceptions.Enqueue(new InvalidOperationException(string.Format("Stream at path: {0} already exists. Please set overwrite to true to overwrite streams that exist.", file.TargetStreamPath)));
                }
            });

            if(exceptions.Count > 0)
            {
                throw new AggregateException(exceptions);
            }
        }

        /// <summary>
        /// Verifies that the metadata is consistent with the local file information.
        /// </summary>
        /// <param name="metadata"></param>
        private void ValidateFolderMetadataMatchesLocalFile(UploadFolderMetadata metadata)
        {
            if (metadata.TargetStreamFolderPath.Trim() != this.Parameters.TargetStreamPath.Trim())
            {
                throw new InvalidOperationException(string.Format("Metadata points to a different target stream folder: {0} than the input parameters: {1}", metadata.TargetStreamFolderPath, this.Parameters.TargetStreamPath));
            }

            //verify that it matches against local file (size, name)
            var metadataInputFileInfo = new DirectoryInfo(metadata.InputFolderPath);
            var paramInputFileInfo = new DirectoryInfo(this.Parameters.InputFilePath);

            if (!paramInputFileInfo.FullName.Equals(metadataInputFileInfo.FullName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The metadata refers to different file than the one requested");
            }

            if (!metadataInputFileInfo.Exists)
            {
                throw new InvalidOperationException("The metadata refers to a file that does not exist");
            }

            var totalBytes = metadataInputFileInfo.GetFiles("*.*", 
                this.Parameters.IsRecursive ?
                SearchOption.AllDirectories : 
                SearchOption.TopDirectoryOnly).Sum(file => file.Length);
            if (metadata.TotalFileBytes != totalBytes)
            {
                throw new InvalidOperationException("The metadata's total size information for all files in the directory differs from the actual directory information!");
            }

            var exceptions = new ConcurrentQueue<Exception>();
            Parallel.ForEach(metadata.Files, file =>
            {
                try
                {
                    ValidateMetadataMatchesLocalFile(file, true);
                }
                catch (Exception e)
                {
                    exceptions.Enqueue(e);
                }
            });

            if(exceptions.Count > 0)
            {
                throw new AggregateException(exceptions);
            }
        }

        /// <summary>
        /// Uploads the file using the given metadata.
        /// 
        /// </summary>
        /// <param name="metadata"></param>
        private void UploadFile(UploadMetadata metadata, IProgress<SegmentUploadProgress> segmentProgressTracker = null)
        {
            try
            {
                segmentProgressTracker = segmentProgressTracker ?? CreateSegmentProgressTracker(metadata);

                if (metadata.SegmentCount == 0)
                {
                    // simply create the target stream, overwriting existing streams if they exist
                    _frontEnd.CreateStream(metadata.TargetStreamPath, true, null, 0);
                }
                else if (metadata.SegmentCount > 1)
                {
                    //perform the multi-segment upload
                    // reducing the thread count to make it equal to the segment count
                    // if it is larger, since those extra threads will not be used.
                    var msu = new MultipleSegmentUploader(metadata, 
                        metadata.SegmentCount < this.Parameters.FileThreadCount ? 
                        metadata.SegmentCount : 
                        this.Parameters.FileThreadCount, 
                        _frontEnd, _token,
                        segmentProgressTracker);
                    msu.UseSegmentBlockBackOffRetryStrategy = this.Parameters.UseSegmentBlockBackOffRetryStrategy;
                    msu.Upload();

                    //concatenate the files at the end
                    ConcatenateSegments(metadata);
                }
                else
                {
                    //optimization if we only have one segment: upload it directly to the target stream
                    metadata.Segments[0].Path = metadata.TargetStreamPath;
                    var ssu = new SingleSegmentUploader(0, metadata, _frontEnd, _token, segmentProgressTracker);
                    ssu.UseBackOffRetryStrategy = this.Parameters.UseSegmentBlockBackOffRetryStrategy;
                    ssu.Upload();
                }
                metadata.Status = SegmentUploadStatus.Complete;

            }
            catch (OperationCanceledException)
            {
                // do nothing since we have already marked everything as failed
            }
        }

        /// <summary>
        /// Creates the segment progress tracker.
        /// </summary>
        /// <param name="metadata">The metadata.</param>
        /// <returns></returns>
        private IProgress<SegmentUploadProgress> CreateSegmentProgressTracker(UploadMetadata metadata, IProgress<UploadProgress> customTracker = null)
        {
            if (_progressTracker == null && customTracker == null)
            {
                return null;
            }
         
            var overallProgress = new UploadProgress(metadata, customTracker);
            return new Progress<SegmentUploadProgress>(
                (sup) =>
                {
                    //update the overall progress and report it back
                    overallProgress.SetSegmentProgress(sup);

                    // used in the folder upload case.
                    if (customTracker != null)
                    {
                        customTracker.Report(overallProgress);
                    }
                    else
                    {
                        _progressTracker.Report(overallProgress);
                    }
                });

        }

        /// <summary>
        /// Creates a file progress tracker.
        /// </summary>
        /// <param name="metadata">The metadata.</param>
        /// <returns></returns>
        private IProgress<UploadProgress> CreateFileProgressTracker(UploadFolderMetadata metadata)
        {
            if (_folderProgressTracker == null)
            {
                return null;
            }

            var overallProgress = new UploadFolderProgress(metadata);
            return new Progress<UploadProgress>(
                (sup) =>
                {
                    //update the overall progress and report it back
                    overallProgress.SetSegmentProgress(sup);
                    _folderProgressTracker.Report(overallProgress);
                });

        }

        /// <summary>
        /// Concatenates all the segments defined in the metadata into a single stream.
        /// </summary>
        /// <param name="metadata"></param>
        private void ConcatenateSegments(UploadMetadata metadata)
        {
            string[] inputPaths = new string[metadata.SegmentCount];
            
            //verify if target stream exists
            if (_frontEnd.StreamExists(metadata.TargetStreamPath))
            {
                if (this.Parameters.IsOverwrite)
                {
                    _frontEnd.DeleteStream(metadata.TargetStreamPath);
                }
                else
                {
                    throw new InvalidOperationException(string.Format("Target Stream: {0} already exists", metadata.TargetStreamPath));
                }
            }

            //ensure all input streams exist and are of the expected length
            //ensure all segments in the metadata are marked as 'complete'
            var exceptions = new List<Exception>();
            Parallel.For(
                0,
                metadata.SegmentCount,
                new ParallelOptions() { MaxDegreeOfParallelism = this.Parameters.FileThreadCount },
                (i) =>
                {
                    try
                    {
                        if (metadata.Segments[i].Status != SegmentUploadStatus.Complete)
                        {
                            throw new UploadFailedException("Cannot perform 'Concatenate' operation because not all streams are fully uploaded.");
                        }

                        var remoteStreamPath = metadata.Segments[i].Path;
                        var retryCount = 0;
                        long remoteLength = -1;
                        
                        while (retryCount < SingleSegmentUploader.MaxBufferUploadAttemptCount)
                        {
                            _token.ThrowIfCancellationRequested();
                            retryCount++;
                            try
                            {
                                remoteLength = _frontEnd.GetStreamLength(remoteStreamPath);
                                break;
                            }
                            catch (Exception e)
                            {
                                _token.ThrowIfCancellationRequested();
                                if (retryCount >= SingleSegmentUploader.MaxBufferUploadAttemptCount)
                                {
                                    throw new UploadFailedException(
                                        string.Format(
                                            "Cannot perform 'Concatenate' operation due to the following exception retrieving file information: {0}",
                                            e));
                                }

                                SingleSegmentUploader.WaitForRetry(retryCount, Parameters.UseSegmentBlockBackOffRetryStrategy, _token);
                            }
                        }

                        
                        if (remoteLength != metadata.Segments[i].Length)
                        {
                            throw new UploadFailedException(string.Format("Cannot perform 'Concatenate' operation because segment {0} has an incorrect length (expected {1}, actual {2}).", i, metadata.Segments[i].Length, remoteLength));
                        }

                        inputPaths[i] = remoteStreamPath;
                    }
                    catch (Exception ex)
                    {
                        //collect any exceptions, whether we just generated them above or whether they come from the Front End,
                        exceptions.Add(ex);
                    }
                });

            if (exceptions.Count > 0)
            {
                throw new AggregateException("At least one concatenate test failed", exceptions.ToArray());
            }

            //issue the command
            _frontEnd.Concatenate(metadata.TargetStreamPath, inputPaths);            
        }
        
        #endregion

    }
}