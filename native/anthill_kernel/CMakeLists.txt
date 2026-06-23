cmake_minimum_required(VERSION 3.16)
project(anthill_kernel LANGUAGES CXX VERSION 1.8.2)

# -----------------------------------------------------------------------------
# ANTHILL native compute kernel.
# Produces a single shared library that the managed Anthill.Core binds to via
# P/Invoke. The output name is deliberately bare ("anthill_kernel") so the .NET
# DllImport("anthill_kernel") resolves to anthill_kernel.dll / libanthill_kernel.so
# / libanthill_kernel.dylib depending on platform.
# -----------------------------------------------------------------------------
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

add_library(anthill_kernel SHARED anthill_kernel.cpp)
target_include_directories(anthill_kernel PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})

if(MSVC)
  target_compile_options(anthill_kernel PRIVATE /O2 /W4 /permissive-)
else()
  target_compile_options(anthill_kernel PRIVATE -O2 -Wall -Wextra -fno-exceptions)
endif()

set_target_properties(anthill_kernel PROPERTIES
  PREFIX "lib"
  OUTPUT_NAME "anthill_kernel")

install(TARGETS anthill_kernel
  RUNTIME DESTINATION bin
  LIBRARY DESTINATION lib)
