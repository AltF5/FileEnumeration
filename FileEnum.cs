// An alternative to Directory.GetFiles(), which operates better, faster, and skips any permission issues
//      Utilizes FindFirstFile & FileNextFile Windows API
//
// Original code from:
//      FastDirectoryEnumerator.cs   |  Aug 2009 |   codeproject.com/Articles/38959/A-Faster-Directory-Enumerator
//
// Description:
//      Utilizes FindFirstFile, FindNextFile
//      As mentioned above, Directory.GetFiles and DirectoryInfo.GetFiles have a number of disadvantages. The most significant is that they throw away information and do not efficiently allow you to retrieve information about multiple files at the same time.
//      Internally, Directory.GetFiles is implemented as a wrapper over the Win32 FindFirstFile/FindNextFile functions. These functions all return information about each file that is enumerated that the GetFiles() method throws away when it returns the file names. They also retrieve information about multiple files with a single network message.
//      The FastDirectoryEnumerator keeps this information and returns it in the FileData class. This substantially reduces the number of network round-trips needed to accomplish the same task.
//
// My notes:
//      - Why utilized: To bypass directories that cause AccessDenied such as the Recycling Bin for root directories when calling Directory.GetFiles
//      -               Also, the newly added EnumerationOptions class require .NET 5, thus not applicable if targeting .NET Framework - stackoverflow.com/a/61868218/5555423
//      - IMPORTANT: Use this class with x64-compiled apps only, otherwise a StackOverflow will occur for every deep directories
//      - My modifications: MoveNext() method to support multiple filters   + Added some cleanup and some regions for simplification
//      - Dec 2020
//
//
// Example call:
//      List<string> GetFilesMultiPattern2(string directory, string searchPatternFilter, bool recursive)
//      {
//          // GetFilesMultiPattern2("K:", "*.mp4|*.mpg|*.mpeg|*.mov|*.wmv|", true);
//          IEnumerable<FileData> fileInfo = FileEnum.EnumerateFiles(directory, searchPatternFilter, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
//          return fileInfo.Select(x => x.Path).ToList();        // Now actually perform the enumeration
//      }


using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Security.Permissions;

#region FileData UDT structure representing massaged data from WIN32_FIND_DATA

/// <summary>
/// Contains information about a file returned by the 
/// <see cref="FileEnum"/> class.
/// </summary>
[Serializable]
public class FileData
{
    /// <summary>
    /// Attributes of the file.
    /// </summary>
    public readonly FileAttributes Attributes;

    public DateTime CreationTime
    {
        get { return this.CreationTimeUtc.ToLocalTime(); }
    }

    /// <summary>
    /// File creation time in UTC
    /// </summary>
    public readonly DateTime CreationTimeUtc;

    /// <summary>
    /// Gets the last access time in local time.
    /// </summary>
    public DateTime LastAccesTime
    {
        get { return this.LastAccessTimeUtc.ToLocalTime(); }
    }
        
    /// <summary>
    /// File last access time in UTC
    /// </summary>
    public readonly DateTime LastAccessTimeUtc;

    /// <summary>
    /// Gets the last access time in local time.
    /// </summary>
    public DateTime LastWriteTime
    {
        get { return this.LastWriteTimeUtc.ToLocalTime(); }
    }
        
    /// <summary>
    /// File last write time in UTC
    /// </summary>
    public readonly DateTime LastWriteTimeUtc;
        
    /// <summary>
    /// Size of the file in bytes
    /// </summary>
    public readonly long Size;

    /// <summary>
    /// Name of the file
    /// </summary>
    public readonly string Name;

    /// <summary>
    /// Full path to the file.
    /// </summary>
    public readonly string Path;

    /// <summary>
    /// Returns a <see cref="T:System.String"/> that represents the current <see cref="T:System.Object"/>.
    /// </summary>
    /// <returns>
    /// A <see cref="T:System.String"/> that represents the current <see cref="T:System.Object"/>.
    /// </returns>
    public override string ToString()
    {
        return this.Name;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileData"/> class.
    /// </summary>
    /// <param name="dir">The directory that the file is stored at</param>
    /// <param name="findData">WIN32_FIND_DATA structure that this
    /// object wraps.</param>
    internal FileData(string dir, WIN32_FIND_DATA findData) 
    {
        this.Attributes = findData.dwFileAttributes;


        this.CreationTimeUtc = ConvertDateTime(findData.ftCreationTime_dwHighDateTime, 
                                            findData.ftCreationTime_dwLowDateTime);

        this.LastAccessTimeUtc = ConvertDateTime(findData.ftLastAccessTime_dwHighDateTime,
                                            findData.ftLastAccessTime_dwLowDateTime);

        this.LastWriteTimeUtc = ConvertDateTime(findData.ftLastWriteTime_dwHighDateTime,
                                            findData.ftLastWriteTime_dwLowDateTime);

        this.Size = CombineHighLowInts(findData.nFileSizeHigh, findData.nFileSizeLow);

        this.Name = findData.cFileName;
        this.Path = System.IO.Path.Combine(dir, findData.cFileName);
    }

    private static long CombineHighLowInts(uint high, uint low)
    {
        return (((long)high) << 0x20) | low;
    }

    private static DateTime ConvertDateTime(uint high, uint low)
    {
        long fileTime = CombineHighLowInts(high, low);
        return DateTime.FromFileTimeUtc(fileTime);
    }
}

#endregion

#region WIN32_FIND_DATA structure

/// <summary>
/// Contains information about the file that is found 
/// by the FindFirstFile or FindNextFile functions.
/// </summary>
[Serializable, StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto), BestFitMapping(false)]
class WIN32_FIND_DATA
{
    public FileAttributes dwFileAttributes;
    public uint ftCreationTime_dwLowDateTime;
    public uint ftCreationTime_dwHighDateTime;
    public uint ftLastAccessTime_dwLowDateTime;
    public uint ftLastAccessTime_dwHighDateTime;
    public uint ftLastWriteTime_dwLowDateTime;
    public uint ftLastWriteTime_dwHighDateTime;
    public uint nFileSizeHigh;
    public uint nFileSizeLow;
    public int dwReserved0;
    public int dwReserved1;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string cFileName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
    public string cAlternateFileName;

    /// <summary>
    /// Returns a <see cref="T:System.String"/> that represents the current <see cref="T:System.Object"/>.
    /// </summary>
    /// <returns>
    /// A <see cref="T:System.String"/> that represents the current <see cref="T:System.Object"/>.
    /// </returns>
    public override string ToString()
    {
        return "File name=" + cFileName;
    }
}

#endregion


#region FileEnum public class

/// <summary>
/// A fast enumerator of files in a directory.  Use this if you need to get attributes for 
/// all files in a directory.
/// </summary>
/// <remarks>
/// This enumerator is substantially faster than using <see cref="Directory.GetFiles(string)"/>
/// and then creating a new FileInfo object for each path.  Use this version when you 
/// will need to look at the attibutes of each file returned (for example, you need
/// to check each file in a directory to see if it was modified after a specific date).
/// </remarks>
public static class FileEnum
{
    // Path skipped : Why
    public static List<Tuple<string, string>> LastCall_SkippedDirectories = new List<Tuple<string, string>>();

    /// <summary>
    /// Gets <see cref="FileData"/> for all the files in a directory that 
    /// match a specific filter, optionally including all sub directories.
    /// </summary>
    /// <param name="path">The path to search.</param>
    /// <param name="searchPattern">The search string to match against files in the path.</param>
    /// <param name="searchOption">
    /// One of the SearchOption values that specifies whether the search 
    /// operation should include all subdirectories or only the current directory.
    /// </param>
    /// <returns>An object that implements <see cref="IEnumerable{FileData}"/> and 
    /// allows you to enumerate the files in the given directory.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="path"/> is a null reference (Nothing in VB)
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="filter"/> is a null reference (Nothing in VB)
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="searchOption"/> is not one of the valid values of the
    /// <see cref="System.IO.SearchOption"/> enumeration.
    /// </exception>
    public static IEnumerable<FileData> EnumerateFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        LastCall_SkippedDirectories.Clear();

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            //return new IEnumerable<FileData>();
        }

        if (string.IsNullOrWhiteSpace(searchPattern))
        {
            searchPattern = "*";
        }

        string fullPath = Path.GetFullPath(path);

        // Setup the Enumerator to have MoveNext calls be called upon initial Results query
        return new FileEnumerable(fullPath, searchPattern, searchOption);
    }

    /// <summary>
    /// Gets <see cref="FileData"/> for all the files in a directory that match a 
    /// specific filter.
    /// </summary>
    /// <param name="path">The path to search.</param>
    /// <param name="searchPattern">The search string to match against files in the path.</param>
    /// <returns>An object that implements <see cref="IEnumerable{FileData}"/> and 
    /// allows you to enumerate the files in the given directory.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="path"/> is a null reference (Nothing in VB)
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="filter"/> is a null reference (Nothing in VB)
    /// </exception>
    public static FileData[] GetFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        LastCall_SkippedDirectories.Clear();

        IEnumerable<FileData> e = FileEnum.EnumerateFiles(path, searchPattern, searchOption);
        List<FileData> list = new List<FileData>(e);

        FileData[] retval = new FileData[list.Count];
        list.CopyTo(retval);

        return retval;
    }


    #region FileEnumerable class

    /// <summary>
    /// Provides the implementation of the 
    /// <see cref="T:System.Collections.Generic.IEnumerable`1"/> interface
    /// </summary>
    class FileEnumerable : IEnumerable<FileData>
    {
        readonly string m_path;
        string m_filterAll;
        readonly SearchOption m_searchOption;

        /// <summary>
        /// Initializes a new instance of the <see cref="FileEnumerable"/> class.
        /// </summary>
        /// <param name="path">The path to search.</param>
        /// <param name="filterAll">The search string to match against files in the path.</param>
        /// <param name="searchOption">
        /// One of the SearchOption values that specifies whether the search 
        /// operation should include all subdirectories or only the current directory.
        /// </param>
        public FileEnumerable(string path, string filterAll = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            m_path = path;
            m_filterAll = filterAll;
            m_searchOption = searchOption;
        }

        #region IEnumerable<FileData> Members

        /// <summary>
        /// Returns an enumerator that iterates through the collection.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Collections.Generic.IEnumerator`1"/> that can 
        /// be used to iterate through the collection.
        /// </returns>
        public IEnumerator<FileData> GetEnumerator()
        {
            return new FileEnumerator(m_path, m_filterAll, m_searchOption);
        }

        #endregion

        #region IEnumerable Members

        /// <summary>
        /// Returns an enumerator that iterates through a collection.
        /// </summary>
        /// <returns>
        /// An <see cref="T:System.Collections.IEnumerator"/> object that can be 
        /// used to iterate through the collection.
        /// </returns>
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return new FileEnumerator(m_path, m_filterAll, m_searchOption);
        }

        #endregion
    }

    #endregion

    #region FileEnumerator class (MoveNext file enumeration implementation)

    #region SafeFileHandle - FindClose API

    /// <summary>
    /// Wraps a FindFirstFile handle.
    /// </summary>
    class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
        [DllImport("kernel32.dll")]
        static extern bool FindClose(IntPtr handle);

        /// <summary>
        /// Initializes a new instance of the <see cref="SafeFindHandle"/> class.
        /// </summary>
        [SecurityPermission(SecurityAction.LinkDemand, UnmanagedCode = true)]
        internal SafeFindHandle()
            : base(true)
        {
        }

        /// <summary>
        /// When overridden in a derived class, executes the code required to free the handle.
        /// </summary>
        /// <returns>
        /// true if the handle is released successfully; otherwise, in the 
        /// event of a catastrophic failure, false. In this case, it 
        /// generates a releaseHandleFailed MDA Managed Debugging Assistant.
        /// </returns>
        protected override bool ReleaseHandle()
        {
            return FindClose(base.handle);
        }
    }

    #endregion

    /// <summary>
    /// Provides the implementation of the 
    /// <see cref="T:System.Collections.Generic.IEnumerator`1"/> interface
    /// </summary>
    [System.Security.SuppressUnmanagedCodeSecurity]
    class FileEnumerator : IEnumerator<FileData>
    {
        #region APIs

        //
        // APIs
        //

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern SafeFindHandle FindFirstFile(string fileName, [In, Out, MarshalAs(UnmanagedType.LPStruct)] WIN32_FIND_DATA data);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool FindNextFile(                     SafeFindHandle hndFindFile, 
                [In, Out, MarshalAs(UnmanagedType.LPStruct)] WIN32_FIND_DATA lpFindFileData);

        #endregion

        #region Search Context - For each path

        /// <summary>
        /// Hold context information about where we current are in the directory search.
        /// </summary>
        private class SearchContext
        {
            public readonly string Path;
            public Stack<string> SubdirectoriesToProcess;
            public List<string> RemainingFilters = new List<string>();

            public SearchContext(string path, List<string> allStartingFilters)
            {
                this.Path = path;

                foreach (string f in allStartingFilters)
                {
                    RemainingFilters.Add(f);
                }
            }
        }

        #endregion

        #region Fields

        string PathToSearch_Start;
        string PathSearchingCurrently;      // Updated for each subdirectory
        List<string> AllFilters = new List<string>();
        SearchOption SearchOption_RecursiveOrNot;
        SafeFindHandle hSearchHandle;
        WIN32_FIND_DATA FindDataIO_CurrentFileFound = new WIN32_FIND_DATA();

        SearchContext CurrentPathContext;
        Stack<SearchContext> PathStack;

        #endregion

        #region CTor (Setup)

        /// <summary>
        /// Initializes a new instance of the <see cref="FileEnumerator"/> class.
        /// </summary>
        /// <param name="path">The path to search.</param>
        /// <param name="filter">The search string to match against files in the path.</param>
        /// <param name="searchOption">
        /// One of the SearchOption values that specifies whether the search 
        /// operation should include all subdirectories or only the current directory.
        /// </param>
        public FileEnumerator(string path, string filter = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            this.PathSearchingCurrently = path;
            this.PathToSearch_Start = path;

            if(!string.IsNullOrWhiteSpace(filter))
            {
                bool wereMultipleFiltersSupplied = filter.Contains("|");

                if (wereMultipleFiltersSupplied)
                {
                    foreach (string f in filter.Split('|'))
                    {
                        if (!string.IsNullOrWhiteSpace(f))
                        {
                            AllFilters.Add(f);
                        }
                    }
                }
                else
                {
                    // Just one
                    AllFilters.Add(filter);
                }
            }
            else if(filter.Trim() == "")
            {
                // At least specify "*" to grab all files
                filter = "*";
                AllFilters.Add("*");
            }

            //m_currentFilter = filter;

            CurrentPathContext = new SearchContext(path, AllFilters);

            // Recursive or Not
            this.SearchOption_RecursiveOrNot = searchOption;
            if (SearchOption_RecursiveOrNot == SearchOption.AllDirectories)
            {
                PathStack = new Stack<SearchContext>();
            }
        }

        #endregion

        /// <summary>
        /// Gets the element in the collection at the current position of the enumerator.
        /// </summary>
        public FileData Current
        {
            get { return new FileData(PathSearchingCurrently, FindDataIO_CurrentFileFound); }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, 
        /// or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            if (hSearchHandle != null)
            {
                hSearchHandle.Dispose();
            }
        }

        /// <summary>
        /// Gets the element in the collection at the current position of the enumerator.
        /// </summary>
        /// <value></value>
        /// <returns>
        /// The element in the collection at the current position of the enumerator.
        /// </returns>
        object System.Collections.IEnumerator.Current
        {
            get { return new FileData(PathSearchingCurrently, FindDataIO_CurrentFileFound); }
        }

        /// <summary>
        /// Advances the enumerator to the next element of the collection.
        /// </summary>
        /// <returns>
        /// true if the enumerator was successfully advanced to the next element; 
        /// false if the enumerator has passed the end of the collection.
        /// </returns>
        /// <exception cref="T:System.InvalidOperationException">
        /// The collection was modified after the enumerator was created.
        /// </exception>
        public bool MoveNext()
        {
            bool success = false;

            // If the handle is null, this is first call to MoveNext in the current 
            // directory.  In that case, start a new search.
            if (CurrentPathContext.SubdirectoriesToProcess == null)
            {
                //
                // New search (ex: for a new subdirectory)
                //


                // FindFileFirst(filter)  +  FindFileNext()
                do
                {
                    if (hSearchHandle == null)
                    {
                        //
                        // this.PathToSearch is updated for each subdirectory under then, when recursive mode is performed
                        //
                        new FileIOPermission(FileIOPermissionAccess.PathDiscovery, PathSearchingCurrently).Demand();


                        string searchPath_WithFilterAdded = PathSearchingCurrently;

                        //
                        // [Changed - to be able to handle multiple filters separated by | ]
                        //      Start the search by first finding a valid filter to utilize
                        //

                        string currentFilter = "";

                        // Load the next filter, to search with ONLY 1 filter at a time
                        if (CurrentPathContext.RemainingFilters.Count > 0)
                        {
                            currentFilter = CurrentPathContext.RemainingFilters[0];
                        }

                        searchPath_WithFilterAdded = Path.Combine(PathSearchingCurrently, currentFilter);

                        FindDataIO_CurrentFileFound = new WIN32_FIND_DATA();
                        hSearchHandle = FindFirstFile(searchPath_WithFilterAdded, FindDataIO_CurrentFileFound);
                        success = !hSearchHandle.IsInvalid;
                        if (!success)
                        {
                            if (CurrentPathContext.RemainingFilters.Count > 0)
                            {
                                CurrentPathContext.RemainingFilters.RemoveAt(0);

                                // No need to invalidate the search handle here, like below with FindNextFile, because 
                                // this search handle was never valid to begin with, right now, since no file with this extension was found
                            }
                        }
                        else
                        {
                            // Successful call with this filter (or no filter), so proceed to FindNextFile...
                        }
                    }
                    else
                    {
                        //
                        // Existing search (same filter)
                        //

                        FindDataIO_CurrentFileFound = new WIN32_FIND_DATA();
                        success = FindNextFile(hSearchHandle, FindDataIO_CurrentFileFound);
                        if (!success)       // No more files exist with this file ext / filter. So move on to the next if available
                        {
                            if (CurrentPathContext.RemainingFilters.Count > 0)
                            {
                                CurrentPathContext.RemainingFilters.RemoveAt(0);

                                // Invalidate the search handle for when the loop returns back to the top
                                // so that a new filter can be supplied
                                hSearchHandle.Close();
                                hSearchHandle = null;
                            }
                        }
                    }

                    // !success means No more files with this ext were found. Success if there WAS a file find (unsure if there are more until the next check)
                    // +
                    // If there is another filter to look at next in this current directory / subdirectory (if this is the recursive call)
                } while (!success && CurrentPathContext.RemainingFilters.Count > 0);
            }

            //If the call to FindNextFile or FindFirstFile succeeded...
            if (success)
            {
                if ((FindDataIO_CurrentFileFound.dwFileAttributes & FileAttributes.Directory) == FileAttributes.Directory)
                {
                    //Ignore folders for now.   We call MoveNext recursively here to 
                    // move to the next item that FindNextFile will return.
                    return MoveNext();
                }
            }
            else if (SearchOption_RecursiveOrNot == SearchOption.AllDirectories)
            {
                if (CurrentPathContext.SubdirectoriesToProcess == null)
                {
                    string[] subDirsNext = new string[0];
                    try
                    {
                        subDirsNext = Directory.GetDirectories(PathSearchingCurrently);
                    }
                    catch (Exception ex)
                    {
                        // Do not process directories that result in System.UnauthorizedAccessException: 'Access to the path 'C:\Program Files\WindowsApps' is denied.'

                        // Track them:
                        LastCall_SkippedDirectories.Add(new Tuple<string, string>(PathSearchingCurrently, ex.ToString()));
                    }

                    CurrentPathContext.SubdirectoriesToProcess = new Stack<string>(subDirsNext);
                }

                if (CurrentPathContext.SubdirectoriesToProcess.Count > 0)
                {
                    string subDir = CurrentPathContext.SubdirectoriesToProcess.Pop();

                    PathStack.Push(CurrentPathContext);
                    this.PathSearchingCurrently = subDir;                                       // Now proceed to the next subdir
                    hSearchHandle = null;
                    CurrentPathContext = new SearchContext(PathSearchingCurrently, this.AllFilters);
                    return MoveNext();
                }

                // If there are no more files in this directory and we are 
                // in a sub directory, pop back up to the parent directory and
                // continue the search from there.
                if (PathStack.Count > 0)
                {
                    CurrentPathContext = PathStack.Pop();
                    PathSearchingCurrently = CurrentPathContext.Path;
                    if (hSearchHandle != null)
                    {
                        hSearchHandle.Close();
                        hSearchHandle = null;
                    }

                    return MoveNext();
                }
            }


            return success;
        }

        /// <summary>
        /// Sets the enumerator to its initial position, which is before the first element in the collection.
        /// </summary>
        /// <exception cref="T:System.InvalidOperationException">
        /// The collection was modified after the enumerator was created.
        /// </exception>
        public void Reset()
        {
            hSearchHandle = null;
        }
    }

    #endregion
}

#endregion
