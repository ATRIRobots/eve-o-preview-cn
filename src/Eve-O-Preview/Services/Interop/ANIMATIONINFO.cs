using System.Runtime.InteropServices;

namespace EveOPreview.Services.Interop
{
    [StructLayout(LayoutKind.Sequential)]
    struct ANIMATIONINFO
    {
        public uint cbSize;
        public int iMinAnimate;
    }
}
