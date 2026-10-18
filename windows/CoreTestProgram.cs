#if CORE_TESTS
using System;
namespace JevTeachWindows {
  // No Windows GUI, UIA provider, real input, microphone or API is constructed here.
  static class CoreTestProgram {
    static int Main() {
      try {
        DesktopEngineTests.Run().GetAwaiter().GetResult();
        NativeSkillTests.Run();
        UiaWorkerTests.Run().GetAwaiter().GetResult();
        return 0;
      } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
  }
}
#endif
