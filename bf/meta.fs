\ vim: cc=64:syntax=forth
\ Virtual target bootstrap engine.

\ This is a port of the Haskell BsForth bootstrapper to
\ BsForth, using the MMU to create an efficient (if potentially
\ crashy) emulator.
---
\ Bootstrap: basics
vocabulary bootstrapper  bootstrapper definitions
variable tgt-base-page  8 tgt-base-page !
  \ Physical page where target image starts.
variable tgt-block  variable tgt-eblock  \ block range
\ Cached target XTs relevant to bootstrapping:
variable tgt-asm-xt    variable tgt-compile-xt
variable tgt-docon-xt  variable tgt-catch-xt
variable tgt-find-xt
: tgt-reset  \ Reset target memory map and vars
  0 tgt-eblock ! 0 tgt-block !
  0 tgt-asm-xt ! 0 tgt-compile-xt ! 0 tgt-docon-xt !
  0 tgt-catch-xt ! 0 tgt-find-xt !
  8 begin
    1-  $C002 io!d  tgt-base-page @ over + $C006 io!  ?dup 0=
  until ;
---
\ Bootstrap: target memory access.
\ We use page 6 in the host memory map as a moving window into
\ target space, like the block buffer. tgt-tran makes a target
\ address visible in the window, and returns its host virtual
\ address.
: tgt-tran  ( t-addr -- addr )
  6 $C002 io!
  dup 13 rshift  tgt-base-page @ +  $C004 io!
  $1FFF and $C000 + ;
---
\ Bootstrap: Sergeant operations and friends.
: <@> tgt-tran @ ;    : <c@> tgt-tran c@ ;
: <!> tgt-tran ! ;    : <c!> tgt-tran c! ;
: <+!> tgt-tran +! ;
: <execute>
  tgt-catch-xt @ ?dup if ['] throw >r then
  \ Assemble a map switch escape sequence at target $FFFA.
  $C000 invert $8000 or  $FFFA <!> $6600 $FFFC <!>
  $713F $FFFE <!>
  \ Arrange return addresses to jump into target, then escape
  $FFFA >r >r   $C000 io!d ;
---
\ Bootstrap: layout of system and user vars in target.
 0 constant <ResetVector>  2 constant <IrqVector>
 4 constant <U0>           6 constant <RootWl>
 8 constant <DP>          10 constant <FREEZEP>
12 constant <VOC-LINK>    14 constant <#sysvars>
: tuser  create , does> @ <U0> <@> + ;
 0 tuser <HANDLER>         2 tuser <STATE>
 4 tuser <'SOURCE>
 8 tuser <>IN>            10 tuser <BASE>
12 tuser <CURRENT>        14 tuser <CONTEXT>
16 tuser <BLK>
: <SOURCE> <'SOURCE> dup <@> swap cell+ <@> ;
\ Lower buffers to dodge emulator trampoline area at $FFFA!
$FBFA constant <blkbuf>  $FB7A constant <initial-U0>
---
\ Bootstrap: target system/user var initialization.
: tgt-init-vars
  \ system variables
  0   <ResetVector> <!>   1        <IrqVector> <!>
  <initial-U0> <U0> <!>   0           <RootWl> <!>
  <#sysvars>   <DP> <!>   <#sysvars> <FREEZEP> <!>
  0      <VOC-LINK> <!>
  \ user area
  0        <HANDLER> <!>  0          <STATE> <!>
  0        <'SOURCE> <!>  0  <'SOURCE> cell+ <!>
  0            <>IN> <!>  10          <BASE> <!>
  <RootWl> <CURRENT> <!>  <RootWl> <CONTEXT> <!>
  0            <BLK> <!> ;
---
\ Bootstrap: mass storage block transfer
: str>tgt  ( c-addr u -- <c-addr> u )
  swap >r
  0 begin
    over over xor
  while ( u i ) ( R: c-addr )
    r@ over + c@  over <blkbuf> + <c!>
    1+
  repeat rdrop drop <blkbuf> swap ;
: block>tgt  ( n -- )
  block 1024 str>tgt 2drop ;
---
\ Bootstrap: using definitions in the target once available.
: ?cached ( addr -- )
  @ ?dup if rdrop <execute> exit then ;
---
\ Bootstrap: target basics
: <here> <DP> <@> ;
: <allot> <DP> <+!> ;
: <raw,>  <here> <!> cell <allot> ;
: <c,> <here> <c!> 1 <allot> ;
: <-,> -1 cells <allot> ;
: <align> <here> aligned <DP> <!> ;
: <freeze> <here> dup <FREEZEP> <!> ;
: <,> <raw,> <freeze> drop ;
---
\ Bootstrap: target emulated assembler
: <asm,> tgt-asm-xt ?cached
  <here> <FREEZEP> <@> xor if <here> cell - <@> ( new prev )
    over $700C = if
      $F04C over and $6000 = if <-,> nip $100C or <asm,> exit
      then $E000 over and $4000 = if <-,> nip $1FFF and <asm,>
      exit then then
    over $F0FF and $6003 over = swap $6000 = or if
      over $0F00 and  dup $200 - $400 u< swap $700 = or if 
        $FFFE over and $6180 = if
          1 and + $FFF3 and dup 3 and 1 = $80 and or
            <-,> <asm,> exit then
      then then
    $6081 over = if over $6C00 = if $FF and or <-,> <asm,> exit
      then then drop
  then <raw,> ;
---
: <literal>
  dup 0< if invert true else false then
  swap $8000 or <asm,> if $6600 <asm,> then ;
: <compile,>    tgt-compile-xt ?cached
  dup <@> $F04C and $700C = if <@> $EFF3 and
  else u2/ $4000 or
  then <asm,> ;
---
\ Bootstrap: target text processing
: <skip-while>  ( <c-addr> u xt -- <c-addr'> u' )
  >r begin over <c@> r@ execute over and
     while 1 /string repeat rdrop ;
: <scan>
  <SOURCE> <>IN> <@> /string
  over >r rot <skip-while>
  2dup 1 min +
  <'SOURCE> <@> - <>IN> <!>
  drop r> tuck - ;
: <skip>
  <SOURCE> <>IN> <@> /string
  rot <skip-while> drop
  <'SOURCE> <@> - <>IN> <!> ;
: <parse-name>  [: bl u<= ;] <skip> [: bl u> ;] <scan> ;
---
: <count> dup 1+ swap <c@> ;
: <s=>  rot over xor if drop 2drop false exit then
  begin ?dup while
    >r  over <c@> over <c@> xor if 2drop rdrop false exit then
    1+ swap 1+ r> 1-
  repeat 2drop true ;
: str>host ( <c-addr> u -- c-addr u )
  flush dup >r begin
    ?dup
  while
    1- over over + <c@> over blkbuf + c!
  repeat drop blkbuf r> ;
---
: <sfoldl>
  >r >r bounds begin
    over over xor while
      dup <c@> r> r@ execute >r
      1+
    repeat 2drop r> rdrop ;
: <s,> dup <c,> 0 [: swap <c,> ;] <sfoldl> drop <align> ;
---
: <find-in>  ( <c-addr> u <wl> -- <c-addr> u 0 | <xt> flags -1)
  begin <@> dup while
    >r  2dup  r@ cell+ <count> <s=> if
      nip  r> cell+ 1+ + aligned   dup cell+ swap <@> true exit
    then r>
  repeat ;
: <find>  tgt-find-xt ?cached
  <CONTEXT> <@> <find-in> if true exit then
  <CURRENT> <@> <find-in> ;
variable 'inside  variable 'outside
: x: CURRENT @ swap CURRENT ! : CURRENT ! ;
: outside: 'outside x: ;
: inside: 'inside x: ;
---
: <]> 1 <STATE> <!> ;   : <[> 0 <STATE> <!> ;
: <exit>  $700C <asm,> ;
---
: tgt-cache!  ( c-addr u addr -- )
  dup @ if drop 2drop exit then

  >r  2dup  str>tgt <find> if  ( c-addr u xt flags ) ( R: a )
    drop  ." caching " dup .  r> ! type cr
  else  ( c-addr u c-addr u ) ( R: a) 2drop 2drop rdrop then ;
: tgt-rescan
  S" asm," tgt-asm-xt tgt-cache!
  S" compile," tgt-compile-xt tgt-cache!
  S" (docon)" tgt-docon-xt tgt-cache!
  S" catch" tgt-catch-xt tgt-cache!
  S" sfind" tgt-find-xt tgt-cache!
  ;
---
\ Bootstrap: interpret current block
: <interpret>
  <parse-name>  ?dup 0= if drop exit then   ( <c-addr> +u )
  <find> if     ( <xt> flags )
    <STATE> <@> 0= or if <execute> else <compile,> then
  else \ not found  ( <c-addr> +u )
    str>host    ( c-addr +u )
    2dup >r >r  base @ >r  <BASE> <@> base !
    ['] number catch  r> base !
    if \ failed
      2drop r> r> <STATE> <@> if 'inside else 'outside then
      find-in if drop execute else ?? then
    else
      rdrop rdrop <STATE> <@> if <literal> then
    then
  then <interpret> ;
---
\ Bootstrap: outer QUIT loop
: <quit>
  tgt-block @ tgt-eblock @ = if exit then

  tgt-block @  dup ." block " u. cr
               dup block>tgt  <BLK> <!>
  <blkbuf> <'SOURCE> <!> 1024 <'SOURCE> cell+ <!> 0 <>IN> <!>
  <interpret>
  1 tgt-block +!  0 <BLK> <!>
  tgt-rescan
  <quit> ;
---
\ Bootstrap: entry point
: begin-bootstrap ( -- )
  ." starting" cr
  tgt-reset  tgt-init-vars ;
: <thru>
  1+ tgt-eblock !  tgt-block ! <quit> ;
: end-bootstrap
  ." used: " <DP> <@> u. cr
  ;
---
\ Bootstrap: common definitions for inside + out
: <\>
  <BLK> <@> if <>IN> <@> 63 + 63 invert and <>IN> <!>
  else <SOURCE> nip <>IN> <!> then ;
: <(> [: ')' <> ;] <scan> 2drop ;
: <:>
  <align> <here>  <CURRENT> <@>  dup <@> <,>  <!>
  <parse-name> <s,>
  0 <,> <]> ;
---
\ Bootstrap: bootstrap control flow words
: <mark>> <freeze> swap <asm,> ;
: <>resolve> dup <@> <freeze> u2/ or swap <!> ;
---
\ Bootstrap: outside definitions.
outside: \    <\> ;     outside: ( <(> ;   outside: host.  . ;
outside: asm, <asm,> ;  outside: , <,> ;   outside: c, <c,> ;
outside: ]    <]> ;
outside: : <:> ;
outside: constant <:> <[> tgt-docon-xt @ <compile,> <,> ;
---
\ Bootstrap: inside definitions.
inside: \ <\> ;   inside: ( <(> ;    inside: [ <[> ;
inside: exit <exit> ;
inside: ; <exit> <[> ;
inside: postpone
  <parse-name> dup if
    <find> if
      if <compile,>
      else <literal>
         tgt-compile-xt @  dup 0= 1 and throw  <compile,>
      then exit
    then
  then ?? ;
inside: if $2000 <mark>> ;   inside: then <>resolve> ;
inside: else 0 <mark>> swap <>resolve> ;
---
remarker clear-target
---