namespace Ralph.Services;

/// <summary>
/// 사용자가 직접 조치해야 하는 환경/설정 오류. Program.cs 최상단에서 잡아
/// stack trace 없이 깔끔히 exit 1을 반환하기 위한 마커 예외.
/// 이 예외는 던지기 전에 호출자가 이미 안내 메시지를 출력했음을 전제한다.
/// </summary>
public sealed class RalphUserException : Exception
{
    public RalphUserException(string message) : base(message) { }
}
