using Microsoft.UI.Xaml;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.System.UserProfile;

namespace VRAMonitor.Services
{
    public static class LanguageSelectorService
    {
        public static void Initialize()
        {
            // 1. 获取用户保存的语言设置 (索引)
            var langIndex = SettingsManager.LanguageIndex;
            string langTag = "System"; // 默认 0 = Follow System

            // 映射：
            // 0 -> Follow System
            // 1 -> zh-CN
            // 2 -> en-US

            if (langIndex == 1) langTag = "zh-CN";
            else if (langIndex == 2) langTag = "en-US";
            // 如果索引是 0 或其他未知值，保持 "System"

            // 2. 应用语言覆盖
            SetLanguageAsync(langTag);
        }

        public static async Task SetLanguageAsync(string languageTag)
        {
            string targetTag = languageTag;

            // 如果请求的是跟随系统，计算实际目标语言
            if (languageTag == "System" || string.IsNullOrEmpty(languageTag))
            {
                targetTag = GetLanguageTagFromSystem();
            }

            // 设置主语言覆盖
            ApplicationLanguages.PrimaryLanguageOverride = targetTag;

            await Task.CompletedTask;
        }

        // [新增] 自定义系统语言判断逻辑
        private static string GetLanguageTagFromSystem()
        {
            // 获取用户首选语言列表
            var systemLangs = GlobalizationPreferences.Languages;
            if (systemLangs != null && systemLangs.Count > 0)
            {
                var firstLang = systemLangs[0].ToLowerInvariant();

                // 如果首选语言是中文 (无论是 zh-CN, zh-TW, zh-HK 等)
                if (firstLang.StartsWith("zh"))
                {
                    return "zh-CN";
                }
            }

            // 其他情况 (非中文) 默认为英文
            return "en-US";
        }
    }
}