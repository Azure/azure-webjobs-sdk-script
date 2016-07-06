﻿using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using System.Web;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Kudu
{
    public class KuduEnvironment : IEnvironment
    {
        private readonly string _dataPath;
        private readonly string _applicationLogFilesPath;
        private readonly string _logFilesPath;
        private readonly string _tempPath;
        private readonly HttpRequestMessage _httpRequestMessage;

        public KuduEnvironment(WebHostSettings settings, HttpRequestMessage httpRequestMessage)
        {
            RootPath = settings.ScriptPath;
            SiteRootPath = settings.ScriptPath;

            _httpRequestMessage = httpRequestMessage;
            _tempPath = Path.GetTempPath();
            _logFilesPath = Path.Combine(_tempPath, "LogFiles");
            _applicationLogFilesPath = _logFilesPath;
            _dataPath = Path.Combine(settings.ScriptPath, Constants.DataPath);

            FileSystemHelpers.EnsureDirectory(_dataPath);
        }


        public string RootPath
        {
            get;
            private set;
        }

        public string SiteRootPath
        {
            get;
            private set;
        }



        public string ApplicationLogFilesPath
        {
            get
            {
                return _applicationLogFilesPath;
            }
        }


        public string DataPath
        {
            get
            {
                return _dataPath;
            }
        }

        public string FunctionsPath
        {
            get
            {
                return this.RootPath;
            }
        }

        public string AppBaseUrlPrefix
        {
            get
            {
                var url = _httpRequestMessage.RequestUri.GetLeftPart(UriPartial.Authority);

                if (string.IsNullOrEmpty(url))
                {
                    // if call is not done in Request context (eg. in BGThread), fall back to %host%
                    var host = System.Environment.GetEnvironmentVariable(Constants.HttpHost);
                    if (!string.IsNullOrEmpty(host))
                    {
                        return $"https://{host}";
                    }

                    throw new InvalidOperationException("There is no request context");
                }
                return url;
            }
        }

        public static bool IsAzureEnvironment()
        {
            return !String.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"));
        }

        public static string GetFreeSpaceHtml(string path)
        {
            try
            {
                ulong freeBytes;
                ulong totalBytes;
                GetDiskFreeSpace(path, out freeBytes, out totalBytes);

                var usage = Math.Round(((totalBytes - freeBytes) * 100.0) / totalBytes);
                var color = usage > 97 ? "red" : (usage > 90 ? "orange" : "green");
                return String.Format(CultureInfo.InvariantCulture, "<span style='color:{0}'>{1:#,##0} MB total; {2:#,##0} MB free</span>", color, totalBytes / (1024 * 1024), freeBytes / (1024 * 1024));
            }
            catch (Exception ex)
            {
                return ex.ToString();
            }
        }

        static void GetDiskFreeSpace(string path, out ulong freeBytes, out ulong totalBytes)
        {
            ulong diskFreeBytes;
            if (!EnvironmentNativeMethods.GetDiskFreeSpaceEx(path, out freeBytes, out totalBytes, out diskFreeBytes))
            {
                throw new Win32Exception();
            }
        }

        [SuppressUnmanagedCodeSecurity]
        static class EnvironmentNativeMethods
        {
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetDiskFreeSpaceEx(string path, out ulong freeBytes, out ulong totalBytes, out ulong diskFreeBytes);
        }

        /// <summary>
        /// Site name could be:
        ///     ~1{actual name}__f0do
        ///     mobile${actual name}
        /// 
        /// We only interested in the {actual name}
        /// </summary>
        private static string NormalizeSiteName(string siteName)
        {
            var normalizedSiteName = siteName;
            if (normalizedSiteName.StartsWith("~1", StringComparison.Ordinal))
            {
                normalizedSiteName = normalizedSiteName.Substring(2);
            }

            normalizedSiteName = Regex.Replace(normalizedSiteName, "__[0-9a-f]{4}$", string.Empty).Replace("mobile$", string.Empty);
            return normalizedSiteName;
        }
    }
}
