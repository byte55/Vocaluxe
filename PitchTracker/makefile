objects = \
	compatibility.o \
	dywapitchtrack/dywapitchtrack.o \
	dywapitchtrack/ptDyWa.o \
	FFT/FFT.o \
	FFT/RealFFTf.o \
	Helper.o \
	performous/pitch.o \
	ptAKF.o \
	PitchWrapper.o

CPPFLAGS = -std=gnu++11 -fPIC

# The file name is not cosmetic: the game imports these functions as
# [DllImport("PitchTracker.dll")], and on Unix .NET looks for that name with a "lib" prefix and the
# platform's shared-library suffix appended - libPitchTracker.dll.so here, .dylib on macOS.
uname := $(shell uname)

ifeq ($(uname),Darwin)
    library = libPitchTracker.dll.dylib
    # macOS builds a dynamic library with -dynamiclib, not -shared. strip needs -x: a full strip
    # would take the exported symbols with it and every P/Invoke would fail at load time.
    link = $(CXX) -dynamiclib -o $(library) $(objects) -lm
    strip_library = strip -x $(library)
else
    library = libPitchTracker.dll.so
    link = g++ -shared -o $(library) -fPIC $(objects) -lm
    strip_library = strip $(library)
endif

PitchTracker: $(objects)
	$(link)
	$(strip_library)
	cp $(library) ../Output/

clean:
	rm -f ../Output/$(library) $(library) $(objects)