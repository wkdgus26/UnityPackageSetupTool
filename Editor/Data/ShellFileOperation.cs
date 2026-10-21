using System;
using System.Runtime.InteropServices;

namespace PackageSetupTool
{
    // Windows SHFileOperationW 의 인자(SHFILEOPSTRUCTW). 필드 순서·형식·이름은 Win32 정의를 따른다.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ShellFileOperation
    {
        public IntPtr hwnd;
        public uint wFunc;
        // 경로 목록. 각 경로 끝과 목록 끝을 null 문자로 끝내야 한다.
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }
}
