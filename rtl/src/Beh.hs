{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE DataKinds #-}
{-# LANGUAGE FlexibleContexts #-}
{-# LANGUAGE BinaryLiterals #-}
{-# LANGUAGE TypeApplications #-}
{-# LANGUAGE ViewPatterns #-}
{-# LANGUAGE TypeFamilies #-}
module Beh where

import Clash.Prelude hiding (Word, cycle)

import Data.Tuple (swap)
import Control.Lens
import Control.Monad.State
import Control.Monad.Reader

import Inst
import CoreInterface

datapath :: MS -> IS -> (MS, OS)
datapath m = swap . runReader (runStateT cycle m)

cycle :: (MonadState MS m, MonadReader IS m) => m OS
cycle = do
  lf <- use msLoadFlag
  if lf
    then finishLoad
    else executeNormally

finishLoad :: (MonadState MS m, MonadReader IS m) => m OS
finishLoad = do
  lastSpace <- use msLastSpace
  assign msT =<< view (case lastSpace of
                         ISpace -> isIData
                         MSpace -> isMData)
  msLoadFlag .= False
  fetch
    <&> osFetch .~ False

executeNormally :: (MonadState MS m, MonadReader IS m) => m OS
executeNormally = do
  inst <- views isMData unpack
  case inst of
    Lit v -> do
      t <- use msT
      msT .= zeroExtend v -- load literal
      msDPtr += 1 -- push data stack
      next
        <&> osDOp . _2 .~ 1
        <&> osDOp . _3 .~ Just t -- flush old T value

    NotLit (Jump tgt) -> do
      msPC .= zeroExtend tgt
      fetch

    NotLit (JumpZ tgt) -> do
      z <- (== 0) <$> use msT   -- test current T value for zero
      msDPtr -= 1               -- pop data stack
      assign msT =<< view isDData -- "

      pc' <- (+ 1) <$> use msPC
      msPC .= if z then zeroExtend tgt else pc'
      fetch

    NotLit (Call tgt) -> do
      pc' <- (+ 1) <$> use msPC
      msPC .= zeroExtend tgt
      msRPtr += 1  -- push return stack
      fetch
        <&> osROp . _2 .~ 1
        <&> osROp . _3 .~ Just (low ++# pc' ++# low)

    NotLit (ALU rpc t' tn tr nm _ rd dd) -> do
      n <- view isDData
      r <- view isRData
      t <- use msT
      pc <- use msPC

      -- Bit 12: R -> PC
      let pc' = if rpc
                  then slice d14 d1 r
                  else pc + 1

      msPC .= pc'

      -- Bit 7: T -> N
      let dop = if tn
                  then Just t
                  else Nothing

      -- Bit 6: T -> R
      let rop = if tr
                  then Just t
                  else Nothing
      -- Bit 5: N -> [T]
      let space = unpack (msb t)
      let write = if nm
                    then Just (space, slice d14 d1 t, n)
                    else Nothing

      let busReq = if t' == MemAtT
                     then case space of
                            MSpace -> MReq (slice d14 d1 t) write
                            ISpace -> IReq (slice d14 d1 t)
                     else MReq pc' write

      msLoadFlag .= (t' == MemAtT)
      msLastSpace .= space

      depth <- use msDPtr

      msDPtr += pack (signExtend dd)
      msRPtr += pack (signExtend rd)

      msT .= case t' of
        T        -> t
        N        -> n
        TPlusN   -> t + n
        TAndN    -> t .&. n
        TOrN     -> t .|. n
        TXorN    -> t `xor` n
        NotT     -> complement t
        NEqT     -> signExtend $ pack $ n == t
        NLtT     -> signExtend $ pack $ unpack @(Signed 16) n < unpack t
        NRshiftT -> n `shiftR` fromIntegral (slice d3 d0 t)
        NMinusT  -> n - t
        R        -> r
        MemAtT   -> errorX "value will be loaded next cycle"
        NLshiftT -> n `shiftL` fromIntegral (slice d3 d0 t)
        Depth    -> zeroExtend depth
        NULtT    -> signExtend $ pack $ n < t

      outputs
        <&> osBusReq .~ busReq
        <&> osDOp . _2 .~ dd
        <&> osDOp . _3 .~ dop
        <&> osROp . _2 .~ rd
        <&> osROp . _3 .~ rop
        <&> osFetch .~ True

next :: (MonadState MS m) => m OS
next = do
  msPC += 1
  fetch

fetch :: (MonadState MS m) => m OS
fetch = do
  pc <- use msPC
  outputs
    <&> osBusReq .~ MReq pc Nothing
    <&> osFetch .~ True

outputs :: (MonadState MS m) => m OS
outputs = do
  dsp <- use msDPtr
  rsp <- use msRPtr
  pure $ OS (errorX "busreq undefined")
            (dsp, 0, Nothing)
            (rsp, 0, Nothing)
            (errorX "fetch undefined")
