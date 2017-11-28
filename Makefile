all:

clean:
	-rm -f build/*

syn: build/icestick-2k.asc

program: build/icestick-2k-prog.bin
	iceprog $<

build:
	mkdir build

build/test.hex: build tools/test.fs
	cd tools && \
	  stack --silent setup && \
	  stack --silent build && \
	  stack --silent exec tools-exe test.fs ../build/test.hex

%.readmemb: %.hex
	ruby -e 'STDIN.each_line { |line| printf("%016b\n", line.to_i(16)) }' \
	  < $< \
	  > $@

build/icestick-2k.asc: build
	cd rtl && \
	  stack --silent setup && \
	  stack --silent build --only-dependencies
	rtl/syn/syn-1k.sh
	cp rtl/syn/out/syn1k.asc build/icestick-2k.asc

build/icestick-2k-prog.asc: build/icestick-2k.asc build/test.hex
	icebram -v rtl/syn/random-2k.hex build/test.hex \
	  < build/icestick-2k.asc \
	  > build/icestick-2k-prog.asc

%.bin: %.asc
	icepack < $< > $@

FORCE:

.PHONY: all clean syn
