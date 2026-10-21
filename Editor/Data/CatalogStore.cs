using System;
using System.IO;

namespace PackageSetupTool
{
    // 공용 폴더의 카탈로그(<공용 폴더>/Catalog.json) 읽기·변경 저장. 파일이 없으면 빈 카탈로그다.
    // 다른 에디터도 같은 파일을 고칠 수 있어, 변경은 항상 파일 최신본을 읽어 그 위에 적용한 뒤 쓴다.
    // 읽을 수 없는 파일은 덮어쓰지 않는다. 카탈로그에만 있는 항목(직접 추가한 git URL)을 잃지 않기 위함이다.
    public static class CatalogStore
    {
        // 읽지 못하면 빈 카탈로그와 사유를 돌려준다.
        public static LocalizedText Load(out PackageCatalog catalog)
        {
            LocalizedText error = Read(out catalog);
            return error == null ? null : new LocalizedText("data.unreadableFile", SharedDataFolder.CatalogFilePath, error);
        }

        // 카탈로그를 바꿀 수 없는 사유. 바꿀 수 있으면 null
        public static LocalizedText GetWriteBlockReason()
        {
            LocalizedText error = Read(out _);
            return error == null ? null : new LocalizedText("data.catalogUnreadable", SharedDataFolder.CatalogFilePath, error);
        }

        public static LocalizedText Update(Action<PackageCatalog> change)
        {
            string path = SharedDataFolder.CatalogFilePath;
            LocalizedText error = Read(out var catalog);
            if (error != null)
            {
                return new LocalizedText("data.catalogUnreadable", path, error);
            }
            change(catalog);
            error = PresetJsonSerializer.WriteFile(path, catalog);
            return error == null ? null : new LocalizedText("data.saveFailed", path, error);
        }

        // 읽지 못하면 빈 카탈로그와 파일 내용의 사유
        private static LocalizedText Read(out PackageCatalog catalog)
        {
            string path = SharedDataFolder.CatalogFilePath;
            if (!File.Exists(path))
            {
                catalog = new PackageCatalog();
                return null;
            }
            LocalizedText error = PresetJsonSerializer.ReadCatalog(path, out catalog);
            catalog ??= new PackageCatalog();
            return error;
        }
    }
}
