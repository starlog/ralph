class Ralph < Formula
  desc "Parallel CLI task orchestrator for Claude Code (git-worktree based)"
  homepage "https://github.com/starlog/ralph"
  version "1.2"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/starlog/ralph/releases/download/v#{version}/ralph-v#{version}-osx-arm64.tar.gz"
      sha256 "5f040f4fdefca0c49c537d4c625f037f4f84c6444355914a172edb51879c47f7"
    end
    on_intel do
      url "https://github.com/starlog/ralph/releases/download/v#{version}/ralph-v#{version}-osx-x64.tar.gz"
      sha256 "6c364936a34b2e37b5ce3310d03c026a977177fa66a0f69d0c1aa7cd478aaae7"
    end
  end

  on_linux do
    on_intel do
      url "https://github.com/starlog/ralph/releases/download/v#{version}/ralph-v#{version}-linux-x64.tar.gz"
      sha256 "8b4b671d033b5f6f570c05d8610562497a5a11a7bdc06903d063d648c8b65f90"
    end
  end

  def install
    bin.install "ralph"
  end

  test do
    assert_match "RALPH", shell_output("#{bin}/ralph --help")
  end
end
