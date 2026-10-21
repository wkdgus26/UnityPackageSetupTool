using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace PackageSetupTool
{
    // 프로젝트 밖 파일을 OS 휴지통으로 보낸다. Unity 의 MoveAssetToTrash 는 프로젝트 안 에셋만 받아 OS별로 처리한다.
    // 실패하면 사유, 성공하면 null 을 돌려준다.
    public static class FileTrash
    {
        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOERRORUI = 0x0400;
        // 휴지통이 없는 드라이브(네트워크 등)라 영구 삭제가 될 때는 확인 창을 띄운다.
        private const ushort FOF_WANTNUKEWARNING = 0x4000;

        public static LocalizedText MoveToTrash(string path)
        {
            return Application.platform switch
            {
                RuntimePlatform.WindowsEditor => MoveToRecycleBin(path),
                RuntimePlatform.OSXEditor => MoveToMacTrash(path),
                _ => new LocalizedText("trash.unsupported", Application.platform),
            };
        }

        private static LocalizedText MoveToRecycleBin(string path)
        {
            var operation = new ShellFileOperation
            {
                wFunc = FO_DELETE,
                pFrom = $"{Path.GetFullPath(path)}\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_WANTNUKEWARNING,
            };
            int result = SHFileOperation(ref operation);
            if (result != 0 || operation.fAnyOperationsAborted)
            {
                return new LocalizedText("trash.failed", path, $"0x{result:X}");
            }
            return null;
        }

        // Finder 의 '되돌려 놓기' 정보는 남지 않지만 휴지통에서 꺼낼 수 있다.
        private static LocalizedText MoveToMacTrash(string path)
        {
            string trashFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".Trash");
            string targetPath = Path.Combine(trashFolderPath, Path.GetFileName(path));
            if (File.Exists(targetPath))
            {
                targetPath = Path.Combine(trashFolderPath, $"{Path.GetFileNameWithoutExtension(path)} {DateTime.Now:yyyyMMddHHmmss}{Path.GetExtension(path)}");
            }
            // 파일 이동 예외(권한·잠김)는 정상 흐름에서 생길 수 있어 사유로 돌려준다.
            try
            {
                File.Move(path, targetPath);
            }
            catch (IOException e)
            {
                return new LocalizedText("trash.failed", path, e.Message);
            }
            catch (UnauthorizedAccessException e)
            {
                return new LocalizedText("trash.failed", path, e.Message);
            }
            return null;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref ShellFileOperation operation);
    }
}
