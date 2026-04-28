using Xunit;

namespace Ralph.Tests;

// CostTracker는 process-wide static 상태를 가지므로 동시 실행이 위험.
// 이 collection에 속한 모든 클래스는 직렬로 실행된다.
[CollectionDefinition("cost")]
public class CostCollection { }
