// Logical coordinates are fixed so a control preview and OBS agree on the end.
export function frame(elapsedSeconds, contentHeight, speed, loop = false) {
  if (loop && contentHeight <= 0) return {y:1080, finished:true};
  const duration = (1080 + contentHeight) / speed;
  const elapsed = Math.max(0, elapsedSeconds);
  const y = 1080 - (loop ? elapsed % duration : elapsed) * speed;
  if (loop) return {y, finished:false};
  return {y, finished:y + contentHeight <= 0};
}
