cmake_minimum_required(VERSION 3.20)
project(colorimetric VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
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
add_executable(bench bench/bench.cpp)
target_link_libraries(bench PRIVATE colorimetric opencv_core opencv_imgproc opencv_imgcodecs)

enable_testing()
add_test(NAME parity COMMAND parity_test ${CMAKE_BINARY_DIR}/fixtures)
