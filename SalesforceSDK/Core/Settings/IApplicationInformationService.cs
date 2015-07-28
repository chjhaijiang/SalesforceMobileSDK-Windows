﻿using System.Threading.Tasks;

namespace Core.Settings
{
    public interface IApplicationInformationService
    {
        Task<string> GetApplicationDisplayNameAsync();

        Task<string> GenerateUserAgentHeaderAsync();

        Task<string> ReadApplicationFileAsync(string path);

        void SaveConfigurationSettings(string config);

        string GetConfigurationSettings();

        void ClearConfigurationSettings();
    }
}
