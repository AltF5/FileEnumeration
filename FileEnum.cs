
// WhoAmI:
//      This class is an alternative to Directory.GetFiles(), which operates better, faster, and skips any permission issues
//      Utilizes FindFirstFile & FileNextFile Windows API
//
// Original code from:
//      FastDirectoryEnumerator.cs ! MoveNext() method  |  Aug 2009 |   https://www.codeproject.com/Articles/38959/A-Faster-Directory-Enumerator   
//
//      This code worked well as a roll-your-own Directory.GetFiles() to skip errors like that of the Recycling Bin
//      However it needed the following improvements:
//          > Recursion --> Iterative, so it has an unlimited stack for long directory trees not overflowing the callstack (which DID happen, especially for x86 compiled apps)
//          > Multiple filter handling for multiple *.FileExts separated by pipes: |
//
//      Description from original code 
//              As mentioned above, Directory.GetFiles and DirectoryInfo.GetFiles have a number of disadvantages. The most significant is that they throw away information and do not efficiently allow you to retrieve information about multiple files at the same time.
//              Internally, Directory.GetFiles is implemented as a wrapper over the Win32 FindFirstFile/FindNextFile functions. These functions all return information about each file that is enumerated that the GetFiles() method throws away when it returns the file names. They also retrieve information about multiple files with a single network message.
//              The FastDirectoryEnumerator keeps this information and returns it in the FileData class. This substantially reduces the number of network round-trips needed to accomplish the same task.
//
// Additional credits & research:
//      Hat-tip to this article for showing how to convert Reclusion --> Iteration    https://docs.microsoft.com/en-us/dotnet/csharp/programming-guide/file-system/how-to-iterate-through-a-directory-tree
//      Also to here mentioning how Recursion is unprofessional and not proper        https://stackoverflow.com/a/19109087/5555423
//          "I occasionally use recursion but only when the call depth is defined and low (as in less than 100). When creating commercial software, using recursive algorithms that have an indefinite number of iterations is completely unprofessional and likely to give you very angry customers."
//          "Even if you manage to get greater recursion depths, simply for performance reasons I would implement this algorithm without recursion. Method calls are way more expensive than iterations within a while loop. I'd strongly advise against implementing anything that requires fiddling with the default stack size."
//
// My notes:
//      - Why utilized: To bypass directories that cause AccessDenied such as the Recycling Bin for root directories when calling Directory.GetFiles
//                      Also, the newly added EnumerationOptions (to skip directories) class require .NET 5, thus not applicable if targeting .NET Framework - stackoverflow.com/a/61868218/5555423
//      - Created:      Dec 2020
//
// Example call:
//      List<string> GetFilesMultiPattern2(string directory, string searchPatternFilter, bool recursive)
//      {
//          // GetFilesMultiPattern2("K:", "*.mp4|*.mpg|*.mpeg|*.mov|*.wmv|", true);
//          List<FileEnumIterative.FileData> fileInfo = FileEnumIterative.EnumerateFiles(directory, searchPatternFilter, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
//          return fileInfo.Select(x => x.Path).ToList();        // Now actually perform the enumeration
//      }

using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Security.Permissions;

#region FileEnumIterative public class

/// <summary>
/// File enumerator utilizing an iterative stack (as to not overflow the callstack with recursion)
/// Created to allow skipping directories which cause errors.
/// </summary>
/// <remarks>
/// This enumerator is substantially faster than using <see cref="Directory.GetFiles(string)"/>
/// and then creating a new FileInfo object for each path.  Use this version when you 
/// will need to look at the attibutes of each file returned (for example, you need
/// to check each file in a directory to see if it was modified after a specific date).
/// </remarks>
public static class FileEnumIterative
{
    #region Fields

    static bool IsDebugging = System.Diagnostics.Debugger.IsAttached;

    #endregion

    #region Public methods (EnumerateFiles / GetFiles)

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
    public static List<FileData> EnumerateFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        LastCall_SkippedDirectories.Clear();

        if (string.IsNullOrWhiteSpace(searchPattern))
        {
            searchPattern = "*";
        }

        string fullPath = Path.GetFullPath(path);

        // Setup the Enumerator to have MoveNext calls be called upon initial Results query
        return EnumerateFilesImpl_IterativeDirectoryTreeWalk(fullPath, searchPattern, searchOption);
    }

    /// <summary>
    /// Alias for EnumerateFiles
    /// </exception>
    public static List<FileData> GetFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        return FileEnumIterative.EnumerateFiles(path, searchPattern, searchOption); 
    }

    #endregion

    /// <summary>
    /// 
    /// Directory traversal using iteration
    /// An iterative solution is much safer since it uses a Stack data structure which can grow indefinitely, unlike a callstack being limited
    /// 
    /// It also appears exponentially MUCH faster than recursion as well, since recursion requires a deep call-stack which could possibly be expensive
    /// This method was previously MoveNext() method in Recursive IEnumerable approach in the original code in FastDirectoryEnumerator.cs
    /// </summary>
    /// <returns></returns>
    static List<FileData> EnumerateFilesImpl_IterativeDirectoryTreeWalk(string path, string filter = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        //
        // Setup (previously the ctor for Recursive approach)
        //

        List<FileData> retList = new List<FileData>();             // Return list of all matching files
        string PathToSearch_Start = path;                          // Root path. Constant.
        List<string> AllFilters = new List<string>();              // Original filter list, utilized for each subdirectory
        SafeFindHandle hSearchHandle = new SafeFindHandle();       // For Find[First / Next]File API calls
        bool successFindFirstFile = false;                         // FindFirstFile API calls - Successful or not
        bool successFindNextFile = false;                          // FindNextFile API calls - Successful or not
        SearchContext CurrentPathContext;                          // Represents filters for each Directory & Subdirectories

        // Current file information returned from FindFile[First / Next] API calls
        FileEnumIterative.WIN32_FIND_DATA FindDataIO_CurrentFileFound = new FileEnumIterative.WIN32_FIND_DATA();

        if (!string.IsNullOrWhiteSpace(filter))
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
        else if (filter.Trim() == "")
        {
            // At least specify "*" to grab all files
            filter = "*";
            AllFilters.Add("*");
        }

        CurrentPathContext = new SearchContext(path, AllFilters);





        //
        // Begin - Start at the root
        //

        Stack<string> PathTreeStack = new Stack<string>();
        PathTreeStack.Push(PathToSearch_Start);


        //
        // Iterative approach (instead of Recursion)
        //

        while (PathTreeStack.Count > 0)
        {
            // Obtain either the root path (1st pass) OR all subdirectories pushed onto the growable stack (added at the end of this loop)
            string PathSearchingCurrently = PathTreeStack.Pop();
            CurrentPathContext = new SearchContext(PathSearchingCurrently, AllFilters);

            if (IsDebugging)
            {
                // Just print the directories for now (not bothering with the file name printing)
                Console.WriteLine(PathSearchingCurrently);
            }

            //
            // New  directory search (ex: for a new subdirectory)
            //

            if (hSearchHandle != null)
            {
                hSearchHandle.Close();
                hSearchHandle = null;
            }

            // FindFileFirst(filter)  +  FindFileNext()
            while (true)
            {
                successFindNextFile = false;

                if (hSearchHandle == null || hSearchHandle.IsInvalid)
                {
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
                    successFindFirstFile = !hSearchHandle.IsInvalid;
                    if (!successFindFirstFile)
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
                        // Successful call with this filter (or no filter), so store it, and then proceed to FindNextFile...
                        retList.Add(new FileEnumIterative.FileData(PathSearchingCurrently, FindDataIO_CurrentFileFound));
                    }
                }
                else
                {
                    //
                    // Existing search (same filter) -- a file was found with this current filter
                    //

                    FindDataIO_CurrentFileFound = new WIN32_FIND_DATA();
                    successFindNextFile = FindNextFile(hSearchHandle, FindDataIO_CurrentFileFound);
                    if (!successFindNextFile)       // No more files exist with this file ext / filter. So move on to the next if available
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
                    else
                    {
                        // Store it
                        retList.Add(new FileData(PathSearchingCurrently, FindDataIO_CurrentFileFound));
                    }
                }       


                if((successFindFirstFile && !successFindNextFile && CurrentPathContext.RemainingFilters.Count == 0) ||
                    !successFindFirstFile && !successFindNextFile && CurrentPathContext.RemainingFilters.Count == 0)
                {
                    // No additional files in this directory if 1 file was found for this filter, but there aren't any other files, and the entire filter list has been gone thru
                    break;
                }


            }  // While: loop indefintiely until the condition above is met

            if (searchOption == SearchOption.AllDirectories)
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


                //
                // Iterative approach (instead of Recursion) : Build the indefinitely-large Stack data structure to be popped each next while-loop pass,
                //      by pushing on each subdirectory for this current directory; and repeating the process for every subdirectory-subdirectory thereafter
                //      Hat-tip to this tutorial - https://docs.microsoft.com/en-us/dotnet/csharp/programming-guide/file-system/how-to-iterate-through-a-directory-tree
                //

                foreach (string d in subDirsNext.Reverse())      // Reverse() so that the order is the same as the recursive approach. (would be reversed otherwise, since the directories A --> Z are pushed on, and thus Z at the top of the stack to then be popped first)
                {
                    // Push all subdirectories onto the stack, which will be processed next (thus in reverse order Z - A, however, this is handled by the .Reverse() above to be in the "expected" search order)
                    PathTreeStack.Push(d);
                }
            }
        }           // While loop to process the stack

        // Cleanup
        if (hSearchHandle != null)
        {
            hSearchHandle.Close();
            hSearchHandle = null;
        }

        return retList;
    }

    #region SearchContext UDT - For each path

    /// <summary>
    /// Hold context information about where we current are in the directory search.
    /// </summary>
    class SearchContext
    {
        public readonly string Path;
        //public Stack<string> SubdirectoriesToProcess;
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

    #region Public FileData UDT structure representing massaged data from WIN32_FIND_DATA

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
        public FileData(string dir, FileEnumIterative.WIN32_FIND_DATA findData)
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

        static long CombineHighLowInts(uint high, uint low)
        {
            return (((long)high) << 0x20) | low;
        }

        static DateTime ConvertDateTime(uint high, uint low)
        {
            long fileTime = CombineHighLowInts(high, low);
            return DateTime.FromFileTimeUtc(fileTime);
        }
    }

    #endregion

    #region WIN32_FIND_DATA structure for APIs

    /// <summary>
    /// Contains information about the file that is found 
    /// by the FindFirstFile or FindNextFile functions.
    /// </summary>
    [Serializable, StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto), BestFitMapping(false)]
    public class WIN32_FIND_DATA
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

    #region APIs

    //
    // APIs
    //

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern SafeFindHandle FindFirstFile(string fileName, [In, Out, MarshalAs(UnmanagedType.LPStruct)] WIN32_FIND_DATA data);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern bool FindNextFile(SafeFindHandle hndFindFile,
            [In, Out, MarshalAs(UnmanagedType.LPStruct)] WIN32_FIND_DATA lpFindFileData);

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
        public SafeFindHandle()
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

    #endregion

}

#endregion
