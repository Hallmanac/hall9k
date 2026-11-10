function trim(s){gsub(/^[ \t]+|[ \t]+$/,"",s);return s}
BEGIN{FS=","}
/^ +Process, +(Start|DCStart)/ { pid=strtonum(trim($(18+1))); # fallback below
}
/^ +Pool, +PoolAllocation/ { n=NF; if (index($0,"0x656B6F54")) { a=trim($n); pid=strtonum(trim($10)); alloc[a]=pid; allocn[pid]++ } }
/^ +Pool, +PoolFree/ { n=NF; if (index($0,"0x656B6F54")) { a=trim($n); if (a in alloc) { delete alloc[a]; freedn++ } else freedunk++ } }
END{
  for (a in alloc) unf[alloc[a]]++
  for (p in allocn) { printf "%d %d %d\n", p, allocn[p], unf[p] }
  print "freed-matched",freedn,"freed-unknownalloc",freedunk > "/dev/stderr"
}
