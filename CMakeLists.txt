cmake_minimum_required(VERSION 3.16)
project(soulfuse C)

find_package(PkgConfig REQUIRED)
pkg_check_modules(FUSE3 REQUIRED IMPORTED_TARGET fuse3)
pkg_check_modules(CURL REQUIRED IMPORTED_TARGET libcurl)

add_executable(soulfuse src/main.c)
target_compile_features(soulfuse PRIVATE c_std_11)
target_compile_options(soulfuse PRIVATE -Wall -Wextra -Wpedantic)
target_link_libraries(soulfuse PRIVATE PkgConfig::FUSE3 PkgConfig::CURL)
