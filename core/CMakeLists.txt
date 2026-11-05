# libm17rx - M17 receiver core
# SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
cmake_minimum_required(VERSION 3.16)
project(libm17rx C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release)
endif()

set(LIBM17 ${CMAKE_CURRENT_SOURCE_DIR}/third_party/libm17)
set(CODEC2 ${CMAKE_CURRENT_SOURCE_DIR}/third_party/codec2-mod)
set(UECC ${CMAKE_CURRENT_SOURCE_DIR}/third_party/micro-ecc)

file(GLOB LIBM17_SRC ${LIBM17}/m17.c ${LIBM17}/decode/*.c ${LIBM17}/encode/*.c
                     ${LIBM17}/math/*.c ${LIBM17}/payload/*.c ${LIBM17}/phy/*.c)
file(GLOB CODEC2_SRC ${CODEC2}/src/*.c)

add_library(m17rx SHARED src/m17rx.c src/meta.c src/dsp.c src/crypto.c ${UECC}/uECC.c ${LIBM17_SRC} ${CODEC2_SRC})
target_include_directories(m17rx PUBLIC include PRIVATE src ${LIBM17} ${CODEC2}/inc ${UECC})
# micro-ecc (BSD-2-Clause): secp256r1 only, big-endian keys as in gr-m17
if(NOT MSVC)
    set_source_files_properties(${UECC}/uECC.c PROPERTIES COMPILE_OPTIONS "-w")
endif()
target_compile_definitions(m17rx PRIVATE M17RX_BUILD=1)
set_target_properties(m17rx PROPERTIES C_VISIBILITY_PRESET hidden PREFIX "lib")

if(MSVC)
    target_compile_options(m17rx PRIVATE /W3)
else()
    target_compile_options(m17rx PRIVATE -Wall -Wextra -Wno-unused-parameter)
    if(NOT WIN32)
        target_link_libraries(m17rx PRIVATE m)
    endif()
endif()

if(WIN32)
    # One self-contained DLL: no libgcc / winpthread DLLs needed next to it.
    set_target_properties(m17rx PROPERTIES PREFIX "" OUTPUT_NAME "m17rx")
    if(NOT MSVC)
        target_link_options(m17rx PRIVATE -static -static-libgcc -Wl,--add-stdcall-alias)
    endif()
endif()

option(M17RX_TESTS "Build the test tools" ON)
if(M17RX_TESTS AND NOT WIN32)
    add_executable(m17rx_cli tests/m17rx_cli.c)
    target_link_libraries(m17rx_cli m17rx m)
    target_include_directories(m17rx_cli PRIVATE include)
    # crypto known-answer tests (AES FIPS-197, scrambler, ECDSA)
    add_executable(crypto_kat tests/crypto_kat.c src/crypto.c ${UECC}/uECC.c)
    target_include_directories(crypto_kat PRIVATE src ${UECC})
    set_source_files_properties(${UECC}/uECC.c TARGET_DIRECTORY crypto_kat PROPERTIES COMPILE_OPTIONS "-w")
endif()
