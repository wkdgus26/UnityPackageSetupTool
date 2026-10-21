using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEngine;

namespace PackageSetupTool
{
    // Asset Store에서 받은 .unitypackage는 gzip 헤더의 확장 필드(서브필드 ID "A$")에 상품 정보 JSON을 담고 있다.
    // 내부 API 없이 상품 ID로 로컬 파일을 찾을 때 이 헤더를 읽는다.
    // 압축을 푼 본문은 tar 이고, 항목은 "<GUID>/asset", "<GUID>/asset.meta", "<GUID>/pathname" 으로 이루어진다.
    public static class AssetStorePackageFileReader
    {
        private const string CacheFolderName = "Asset Store-5.x";
        private const string CachePathEnvironmentVariable = "ASSETSTORE_CACHE_PATH";
        private const int GzipHeaderLength = 10;
        private const int GzipExtraFieldFlag = 0x04;

        private const int TarBlockSize = 512;
        private const int TarNameLength = 100;
        private const int TarSizeOffset = 124;
        private const int TarSizeLength = 12;
        private const int TarSkipBufferSize = 81920;
        private const int MaxPathnameBytes = 4096;
        private const int GuidLength = 32;
        private const string AssetEntryName = "asset";
        private const string PathnameEntryName = "pathname";
        private static readonly string[] ProjectPathPrefixes = { "Assets/", "Packages/" };

        // productId → .unitypackage 경로
        public static Dictionary<long, string> ScanCache(string cacheRootPath)
        {
            var result = new Dictionary<long, string>();
            // 내부 캐시 경로 설정·OS 기본 경로는 "Asset Store-5.x" 의 상위 폴더를 가리키고,
            // 환경 변수(ASSETSTORE_CACHE_PATH)는 패키지 폴더 자체를 가리킬 수 있어 둘 다 받아들인다.
            string folder = Path.Combine(cacheRootPath, CacheFolderName);
            if (!Directory.Exists(folder))
            {
                folder = cacheRootPath;
            }
            if (!Directory.Exists(folder))
            {
                // 아직 아무 패키지도 받지 않은 PC
                return result;
            }

            foreach (string path in Directory.EnumerateFiles(folder, "*.unitypackage", SearchOption.AllDirectories))
            {
                long? productId = ReadProductId(path);
                if (productId.HasValue)
                {
                    result[productId.Value] = path.Replace('\\', '/');
                }
            }
            return result;
        }

        // 환경 변수 → OS 기본 경로 순서. 내부 캐시 경로 설정을 쓸 수 없을 때 사용한다.
        public static string GetDefaultCacheRootPath()
        {
            string environmentPath = Environment.GetEnvironmentVariable(CachePathEnvironmentVariable);
            if (!string.IsNullOrEmpty(environmentPath))
            {
                return environmentPath;
            }

            return Application.platform switch
            {
                RuntimePlatform.WindowsEditor => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Unity"),
                RuntimePlatform.OSXEditor => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Unity"),
                _ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".local", "share", "unity3d"),
            };
        }

        // 프로젝트 경로(Assets/, Packages/)에 놓이는 에셋 항목의 GUID. 폴더 항목은 asset 파일이 없어 빠진다.
        // (프로젝트에 이미 있던 폴더는 GUID가 달라 임포트 확인에 쓸 수 없다)
        // 파일 입출력·압축 해제 예외(삭제·잠김·손상)는 정상 흐름에서 생길 수 있어 실패 사유로 돌려준다.
        public static bool TryReadAssetGuids(string packagePath, out List<string> assetGuids, out LocalizedText error)
        {
            assetGuids = null;
            try
            {
                using var stream = new GZipStream(File.OpenRead(packagePath), CompressionMode.Decompress);
                error = ReadTarAssetGuids(stream, out assetGuids);
            }
            catch (IOException e)
            {
                error = new RawText(e.Message);
            }
            catch (UnauthorizedAccessException e)
            {
                error = new RawText(e.Message);
            }
            catch (InvalidDataException e)
            {
                error = new RawText(e.Message);
            }
            return error == null;
        }

        // 파일 입출력 예외(잠김·권한·손상)는 정상 흐름에서 생길 수 있어 해당 파일만 건너뛴다.
        private static long? ReadProductId(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var header = new byte[GzipHeaderLength];
                if (stream.Read(header, 0, GzipHeaderLength) < GzipHeaderLength
                    || header[0] != 0x1f || header[1] != 0x8b
                    || (header[3] & GzipExtraFieldFlag) == 0)
                {
                    return null;
                }

                // 확장 필드 길이(2바이트, LE)가 잘린 파일
                int extraLengthLow = stream.ReadByte();
                int extraLengthHigh = stream.ReadByte();
                if (extraLengthLow < 0 || extraLengthHigh < 0)
                {
                    return null;
                }
                int extraLength = extraLengthLow | (extraLengthHigh << 8);
                var extra = new byte[extraLength];
                if (stream.Read(extra, 0, extraLength) < extraLength)
                {
                    return null;
                }

                // 서브필드 구조: ID 2바이트, 길이 2바이트(LE), 데이터
                int offset = 0;
                while (offset + 4 <= extraLength)
                {
                    int dataLength = extra[offset + 2] | (extra[offset + 3] << 8);
                    if (extra[offset] == 'A' && extra[offset + 1] == '$' && offset + 4 + dataLength <= extraLength)
                    {
                        return ParseProductId(Encoding.UTF8.GetString(extra, offset + 4, dataLength), path);
                    }
                    offset += 4 + dataLength;
                }
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        // 헤더 JSON은 외부 파일 내용이라 형식이 보장되지 않는다. 잘못된 메타데이터는 이 파일만 건너뛰고 원인을 남긴다.
        private static long? ParseProductId(string json, string path)
        {
            AssetStorePackageHeader packageHeader;
            try
            {
                packageHeader = JsonUtility.FromJson<AssetStorePackageHeader>(json);
            }
            catch (ArgumentException e)
            {
                Debug.LogWarning($"[Package Setup] {LocalizationManager.Get("log.packageHeaderUnreadable", path, e.Message)}");
                return null;
            }
            if (packageHeader == null)
            {
                Debug.LogWarning($"[Package Setup] {LocalizationManager.Get("log.packageHeaderEmpty", path)}");
                return null;
            }
            return long.TryParse(packageHeader.id, out long productId) ? productId : null;
        }

        // 실패하면 사유, 성공하면 null
        private static LocalizedText ReadTarAssetGuids(Stream tarStream, out List<string> assetGuids)
        {
            assetGuids = null;
            var assetEntryGuids = new HashSet<string>();
            var pathnames = new Dictionary<string, string>();
            var header = new byte[TarBlockSize];
            var skipBuffer = new byte[TarSkipBufferSize];
            while (true)
            {
                int headerLength = ReadFully(tarStream, header, TarBlockSize);
                // 끝 표시(0으로 채운 블록)나 파일 끝
                if (headerLength == 0 || header.All(x => x == 0))
                {
                    break;
                }
                if (headerLength < TarBlockSize)
                {
                    return new LocalizedText("package.fileTruncated");
                }
                long size = ParseOctal(header, TarSizeOffset, TarSizeLength);
                if (size < 0)
                {
                    return new LocalizedText("package.tarHeaderUnreadable");
                }

                string name = ReadEntryName(header);
                int separatorIndex = name.IndexOf('/');
                string guid = separatorIndex == GuidLength && name.Take(GuidLength).All(Uri.IsHexDigit) ? name.Substring(0, GuidLength) : null;
                string entryName = guid != null ? name.Substring(separatorIndex + 1) : null;
                long paddedSize = (size + TarBlockSize - 1) / TarBlockSize * TarBlockSize;
                if (entryName == PathnameEntryName)
                {
                    if (size > MaxPathnameBytes)
                    {
                        return new LocalizedText("package.tarHeaderUnreadable");
                    }
                    var data = new byte[paddedSize];
                    if (ReadFully(tarStream, data, (int)paddedSize) < paddedSize)
                    {
                        return new LocalizedText("package.fileTruncated");
                    }
                    // pathname 은 첫 줄이 경로다(뒤에 다른 줄이 붙는 패키지가 있다).
                    pathnames[guid] = Encoding.UTF8.GetString(data, 0, (int)size).Split('\n')[0].TrimEnd('\r');
                    continue;
                }

                if (!SkipFully(tarStream, paddedSize, skipBuffer))
                {
                    return new LocalizedText("package.fileTruncated");
                }
                if (entryName == AssetEntryName)
                {
                    assetEntryGuids.Add(guid);
                }
            }

            assetGuids = assetEntryGuids
                .Where(x => pathnames.TryGetValue(x, out string pathname)
                            && ProjectPathPrefixes.Any(prefix => pathname.StartsWith(prefix, StringComparison.Ordinal)))
                .ToList();
            return null;
        }

        // 이름 필드(0으로 끝남). "./" 로 시작하는 tar 도 받아들인다.
        private static string ReadEntryName(byte[] header)
        {
            int length = Array.IndexOf(header, (byte)0, 0, TarNameLength);
            string name = Encoding.ASCII.GetString(header, 0, length < 0 ? TarNameLength : length);
            return name.StartsWith("./") ? name.Substring(2) : name;
        }

        // 8진수 크기 필드. 형식이 다르면 -1
        private static long ParseOctal(byte[] header, int offset, int length)
        {
            long value = 0;
            bool hasDigit = false;
            for (int i = offset; i < offset + length; i++)
            {
                byte b = header[i];
                if (b == 0 || (b == ' ' && hasDigit))
                {
                    break;
                }
                if (b == ' ')
                {
                    continue;
                }
                if (b < '0' || b > '7')
                {
                    return -1;
                }
                value = value * 8 + (b - '0');
                hasDigit = true;
            }
            return hasDigit ? value : -1;
        }

        // 압축 스트림은 요청보다 적게 읽을 수 있어 채울 때까지 읽는다. 읽은 바이트 수를 돌려준다.
        private static int ReadFully(Stream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);
                if (read <= 0)
                {
                    break;
                }
                total += read;
            }
            return total;
        }

        // 압축 스트림은 탐색할 수 없어 읽어서 버린다.
        private static bool SkipFully(Stream stream, long count, byte[] buffer)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (read <= 0)
                {
                    return false;
                }
                count -= read;
            }
            return true;
        }
    }
}
