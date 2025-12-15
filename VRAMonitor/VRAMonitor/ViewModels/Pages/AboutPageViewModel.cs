using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;
using Windows.ApplicationModel;

namespace VRAMonitor.ViewModels.Pages
{
    public class AboutPageViewModel
    {
        public string AppVersion { get; }

        public AboutPageViewModel()
        {
            AppVersion = GetAppVersion();
        }

        /// <summary>
        /// 从 Package.appxmanifest 中获取版本号
        /// </summary>
        private string GetAppVersion()
        {
            try
            {
                Package package = Package.Current;
                PackageId packageId = package.Id;
                PackageVersion version = packageId.Version;

                return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            }
            catch (Exception)
            {
                // 如果是在未打包环境下调试 (Unpackaged)，回退方案
                return "1.0.0.0 (Dev)";
            }
        }
    }
}