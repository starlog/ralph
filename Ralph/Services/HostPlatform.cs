namespace Ralph.Services;

/// <summary>
/// 호스트 OS에 따라 실제로 resolve되는 인터프리터 바이너리 이름을 단일 지점에서 관리한다.
///
/// 동기: Windows에서 `python3.exe`는 `C:\Users\<u>\AppData\Local\Microsoft\WindowsApps\` 경로의
/// Microsoft Store 스텁이 PATH 우선순위로 잡히는 경우가 많다. Store에서 Python을 설치하지 않은
/// 환경에서는 호출 시 stderr에 "Python"만 찍고 exit 9009로 종료 — verification이 코드와 무관하게
/// 항상 실패한다. Anaconda / python.org 설치본은 `python.exe`로 등록되므로 Windows에서는 `python`이
/// 실제 인터프리터일 확률이 압도적으로 높다.
///
/// macOS / Linux 는 `python3` 가 표준이며 시스템 `python` 은 부재하거나 Python 2일 수 있어 권장하지
/// 않는다.
/// </summary>
internal static class HostPlatform
{
    /// <summary>이 머신에서 실행 가능한 Python 인터프리터 명령. plan 생성과 smoke test 추론에서 공유.</summary>
    public static string PythonCommand => OperatingSystem.IsWindows() ? "python" : "python3";

    /// <summary>플랜 프롬프트에 노출할 사람이 읽는 OS 이름.</summary>
    public static string OsName =>
        OperatingSystem.IsWindows() ? "Windows" :
        OperatingSystem.IsMacOS()   ? "macOS"   :
        OperatingSystem.IsLinux()   ? "Linux"   :
        "POSIX";
}
