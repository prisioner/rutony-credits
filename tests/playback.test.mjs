import test from 'node:test';
import assert from 'node:assert/strict';
import {frame} from '../src/RutonyCredits/web/playback.js';
test('scroll keeps the final line visible until it leaves the logical OBS canvas', () => {
  assert.deepEqual(frame(2, 520, 80), {y:920, finished:false});
  const almost = frame(19.99, 520, 80);
  assert.ok(Math.abs(almost.y + 519.2) < 0.00001);
  assert.equal(almost.finished, false);
  assert.deepEqual(frame(20, 520, 80), {y:-520, finished:true});
});
test('resuming uses elapsed show time and speed; clock skew never starts above the bottom', () => {
  assert.deepEqual(frame(10, 800, 120), {y:-120, finished:false});
  assert.deepEqual(frame(-1, 800, 120), {y:1080, finished:false});
});

test('loop restarts at the bottom only after the entire content has left the canvas', () => {
  const almost = frame(19.99, 520, 80, true);
  assert.ok(Math.abs(almost.y + 519.2) < 0.00001);
  assert.equal(almost.finished, false);
  assert.deepEqual(frame(20, 520, 80, true), {y:1080, finished:false});
  assert.deepEqual(frame(22, 520, 80, true), {y:920, finished:false});
});

test('loop reload preserves elapsed phase across multiple cycles and clamps clock skew', () => {
  assert.deepEqual(frame(62, 520, 80, true), {y:920, finished:false});
  assert.deepEqual(frame(40, 520, 80, true), {y:1080, finished:false});
  assert.deepEqual(frame(-3, 520, 80, true), {y:1080, finished:false});
});

test('an empty loop does not stay running', () => {
  assert.deepEqual(frame(0, 0, 80, true), {y:1080, finished:true});
});
