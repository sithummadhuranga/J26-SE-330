/* Emulator-only: LD_PRELOAD this on the Android emulator running on an Apple M4 host.
 * The emulator advertises SVE2/SME in AT_HWCAP/AT_HWCAP2 but cannot execute them, and OpenCV 5's
 * KleidiCV picks its SVE code path from those flags, then dies with SIGILL in a static constructor.
 * Hiding the flags makes it take the NEON path, which is what a Snapdragon 695 uses anyway.
 * Not needed (and not wanted) on a real phone. */
#include <dlfcn.h>
#include <sys/auxv.h>

unsigned long getauxval(unsigned long type) {
  static unsigned long (*real)(unsigned long);
  if (!real) real = (unsigned long (*)(unsigned long))dlsym(RTLD_NEXT, "getauxval");
  unsigned long v = real(type);
  if (type == AT_HWCAP) v &= ~(1UL << 22);                   /* HWCAP_SVE */
  if (type == AT_HWCAP2) v &= ~(0x7eUL | (0x1ffffUL << 23));  /* SVE2 family, SME family */
  return v;
}
