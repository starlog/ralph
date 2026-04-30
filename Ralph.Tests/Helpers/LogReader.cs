namespace Ralph.Tests.Helpers;

internal static class LogReader
{
    // RalphLogger가 파일을 FileAccess.Write로 열고 있는 동안에도 안전하게 읽기 위한 헬퍼.
    // File.ReadAllText[Async]의 기본 share=Read는 Windows에서 기존 Write 핸들과 충돌해
    // IOException을 일으킨다 — FileShare.ReadWrite|Delete로 직접 열어 그 충돌을 회피한다.
    public static async Task<string> ReadOpenLogAsync(string path)
    {
        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return await sr.ReadToEndAsync();
    }

    public static string ReadOpenLog(string path)
    {
        using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }
}
