# =============================================================================
# CMakeLists.txt — Build NDK de la bibliothèque native GARDEN
# Projet : GARDEN — Système de Conscience Temporelle
# Couche : src/GARDEN.Native/c/CMakeLists.txt
#
# Produit libjson_codec.so chargée par P/Invoke côté C# (CodecJsonNatif).
# Cibles ABI requises : arm64-v8a (appareils réels) et x86_64 (émulateur).
# La sélection d'ABI se fait via -DANDROID_ABI au moment de la configuration
# (le plugin Gradle/NDK appelle CMake une fois par ABI).
#
# Build hôte (tests PC) :
#   cmake -S . -B build && cmake --build build && ./build/test_codec
# Build NDK (exemple arm64) :
#   cmake -S . -B build-arm64 \
#     -DCMAKE_TOOLCHAIN_FILE=$ANDROID_NDK/build/cmake/android.toolchain.cmake \
#     -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-26
# =============================================================================

cmake_minimum_required(VERSION 3.18)

project(garden_native LANGUAGES C VERSION 1.0.0)

# --- Norme et durcissement --------------------------------------------------
set(CMAKE_C_STANDARD 90)            # C89/ANSI : portabilité maximale (ESP32 inclus)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)

# Avertissements stricts traités comme erreurs (qualité de build).
add_compile_options(-Wall -Wextra -Werror -Wshadow -Wconversion)

# Durcissement sécurité par défaut (NDK et hôte).
add_compile_options(-fstack-protector-strong -D_FORTIFY_SOURCE=2 -O2)
if(NOT ANDROID)
  # Sur hôte uniquement : facilite l'analyse mémoire en CI.
  option(GARDEN_ASAN "Activer AddressSanitizer/UBSan (hôte)" OFF)
  if(GARDEN_ASAN)
    add_compile_options(-fsanitize=address,undefined -g)
    add_link_options(-fsanitize=address,undefined)
  endif()
endif()

# --- Bibliothèque partagée chargée par le CLR -------------------------------
add_library(json_codec SHARED json_codec.c)
target_include_directories(json_codec PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})

# Intégration crypto en production (NON activée par défaut) :
#   le chiffrement réel est branché via mbedTLS ou l'AES système. Décommenter
#   et fournir le chemin de la lib une fois le choix validé par l'équipe.
# find_package(MbedTLS REQUIRED)
# target_link_libraries(json_codec PRIVATE MbedTLS::mbedcrypto)

# --- Exécutable de test (hôte uniquement) -----------------------------------
if(NOT ANDROID)
  add_executable(test_codec tests/test_json_codec.c json_codec.c)
  target_include_directories(test_codec PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
  enable_testing()
  add_test(NAME codec_unit_tests COMMAND test_codec)
endif()
