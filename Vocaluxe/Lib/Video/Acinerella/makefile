# The C wrapper around ffmpeg that decodes audio and video.
#
#   Linux    libacinerella.so      copied to Output/, loaded from there by the game
#   macOS    libacinerella.dylib   likewise (the P/Invoke name follows the platform, see CAcinerella.cs)
#   Windows  acinerella.dll
#
# Needs the ffmpeg development headers and libraries: libav{format,codec,util}, libsw{scale,resample}.

uname := $(shell uname)

ifeq ($(uname),Darwin)
    # Homebrew's ffmpeg is not on the default search path, and the prefix moved between Intel
    # (/usr/local) and Apple Silicon (/opt/homebrew) - ask pkg-config instead of guessing.
    ffmpeg_packages = libavformat libavcodec libavutil libswscale libswresample
    ffmpeg_cflags := $(shell pkg-config --cflags $(ffmpeg_packages) 2>/dev/null)
    ffmpeg_libs := $(shell pkg-config --libs $(ffmpeg_packages) 2>/dev/null)
    ifeq ($(ffmpeg_libs),)
        # pkg-config is not part of a bare macOS, and Homebrew only pulls it in as someone else's
        # dependency. Without it, go by the prefix brew reports.
        brew_prefix := $(shell brew --prefix 2>/dev/null)
        ffmpeg_cflags = -I$(brew_prefix)/include
        ffmpeg_libs = -L$(brew_prefix)/lib -lavformat -lavcodec -lavutil -lswscale -lswresample
    endif
endif

acinerella: acinerella.c acinerella.h
ifeq ($(uname),Darwin)
	$(CC) -c -fPIC acinerella.c $(ffmpeg_cflags)
	$(CC) -dynamiclib -o libacinerella.dylib acinerella.o $(ffmpeg_libs) -lm
	strip -x libacinerella.dylib
	cp libacinerella.dylib ../../../../Output/
else ifeq ($(uname),Linux)
	gcc -c -fPIC acinerella.c -I /usr/local/include
	gcc -shared -o libacinerella.so acinerella.o -lavformat -lavcodec -lavutil -lm -lswscale -lswresample
	strip libacinerella.so
	cp libacinerella.so ../../../../Output/
else
	gcc -c -fPIC acinerella.c -I /usr/local/include
	gcc -shared -o acinerella.dll -fPIC acinerella.o -lavformat -lavcodec -lavutil -lm -lswscale -lws2_32
	strip acinerella.dll
endif

clean:
	rm -f acinerella.o \
	      libacinerella.so ../../../../Output/libacinerella.so \
	      libacinerella.dylib ../../../../Output/libacinerella.dylib \
	      acinerella.dll
