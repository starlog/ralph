namespace Ralph.Commands;

/// <summary>
/// 모든 ralph 핸들러의 공통 인터페이스. 한 핸들러 = 한 클래스 = 한 단위 테스트.
/// 이전엔 Program.cs의 local async function이라 호출 경로가 통합 테스트뿐이었다.
/// </summary>
public interface ICommand
{
    Task<int> ExecuteAsync(CancellationToken ct);
}
