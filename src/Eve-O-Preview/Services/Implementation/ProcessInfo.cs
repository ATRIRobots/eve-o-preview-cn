using System;

namespace EveOPreview.Services.Implementation
{
    sealed class ProcessInfo : IProcessInfo
    {
        public ProcessInfo(IntPtr handle, string title, string serverName = null)
        {
            this.Handle = handle;
            this.Title = title;
            this.ServerName = serverName ?? string.Empty;
        }

        public IntPtr Handle { get; }
        public string Title { get; }
        public string ServerName { get; }
    }
}