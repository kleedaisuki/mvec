cmake_minimum_required(VERSION 3.20)
project(mvec_vec1 LANGUAGES C)

add_library(vec1 MODULE vec1.c)
set_target_properties(vec1 PROPERTIES PREFIX "" C_STANDARD 11)
target_compile_definitions(vec1 PRIVATE NDEBUG)
if(NOT WIN32)
  find_package(Threads REQUIRED)
  target_link_libraries(vec1 PRIVATE Threads::Threads m)
endif()

# Vec1 dispatches at load time, so x64 binaries also run on non-AVX2 hosts.
if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(x86_64|AMD64|amd64)$"
    AND NOT CMAKE_GENERATOR_PLATFORM MATCHES "ARM|Win32"
    AND NOT CMAKE_OSX_ARCHITECTURES MATCHES "arm64")
  add_library(vec1_avx2 OBJECT vec1.c)
  set_target_properties(vec1_avx2 PROPERTIES POSITION_INDEPENDENT_CODE ON C_STANDARD 11)
  target_compile_definitions(vec1 PRIVATE VEC1SIMD=SCALAR)
  target_compile_definitions(vec1_avx2 PRIVATE VEC1SIMD=AVX2 NDEBUG)
  if(MSVC)
    target_compile_options(vec1_avx2 PRIVATE /arch:AVX2)
  else()
    target_compile_options(vec1_avx2 PRIVATE -mavx2 -mfma)
  endif()
  target_sources(vec1 PRIVATE $<TARGET_OBJECTS:vec1_avx2>)
endif()

if(APPLE)
  set_target_properties(vec1 PROPERTIES SUFFIX ".dylib")
endif()
install(TARGETS vec1 LIBRARY DESTINATION . RUNTIME DESTINATION .)
