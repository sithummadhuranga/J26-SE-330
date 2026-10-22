cmake_minimum_required(VERSION 3.20)
project(colorimetric VERSION 0.1.0 LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

# -DCC_SANITIZE=address,undefined instruments everything we build (see build_android.sh sanitize).
if(CC_SANITIZE)
  # vptr misfires on OpenCV, which the Android SDK builds without RTTI
  add_compile_options(-fsanitize=${CC_SANITIZE} -fno-sanitize=vptr -fno-omit-frame-pointer -g)
  add_link_options(-fsanitize=${CC_SANITIZE})
endif()

# Only the modules the pipeline uses: keeps the Android binary small (imgcodecs only for tests/bench).
find_package(OpenCV REQUIRED COMPONENTS core imgproc photo imgcodecs)

# Stages live in a static library so the tests can call them directly; the shared library
# (what Flutter loads) exports only the C interface.
add_library(colorimetric_core STATIC src/stages.cpp)
set_target_properties(colorimetric_core PROPERTIES POSITION_INDEPENDENT_CODE ON)
target_include_directories(colorimetric_core PUBLIC src)
target_link_libraries(colorimetric_core PUBLIC opencv_core opencv_imgproc opencv_photo)
target_compile_options(colorimetric_core PRIVATE -Wall -Wextra)

add_library(colorimetric SHARED src/pipeline.cpp)
target_include_directories(colorimetric PUBLIC include)
target_link_libraries(colorimetric PRIVATE colorimetric_core)
target_compile_options(colorimetric PRIVATE -Wall -Wextra)

add_executable(parity_test tests/parity_test.cpp)
target_link_libraries(parity_test PRIVATE colorimetric_core colorimetric opencv_imgcodecs)
add_executable(api_test tests/api_test.cpp)
target_link_libraries(api_test PRIVATE colorimetric)
add_executable(bench bench/bench.cpp)
# Emulator on an Apple M4 host only: see tools/hide_sve.c. Built into the test executables so it also wins over
# the sanitizer runtime's own getauxval interceptor.
if(CC_HIDE_SVE)
  foreach(t parity_test api_test bench)
    target_sources(${t} PRIVATE tools/hide_sve.c)
    target_link_options(${t} PRIVATE -Wl,--export-dynamic)
  endforeach()
endif()
target_link_libraries(bench PRIVATE colorimetric opencv_core opencv_imgproc opencv_imgcodecs)

enable_testing()
add_test(NAME api COMMAND api_test)
add_test(NAME parity COMMAND parity_test ${CMAKE_BINARY_DIR}/fixtures)
