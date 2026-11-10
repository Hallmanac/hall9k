function trim(s){gsub(/^[ \t]+|[ \t]+$/,"",s);return s}
BEGIN{FS=","}
/^ +Process, +(Start|DCStart)/ { cp=strtonum(trim($21)); img="?"; if (match($0,/"[^"]+\.[Ee][Xx][Ee]"/)) img=substr($0,RSTART+1,RLENGTH-2); name[cp]=img; next }
/^ +Pool, +(PoolAllocation|PoolFree)/ { tid=strtonum(trim($11)); n=++qt[tid]; qts[tid,n]=trim($17)+0; ad=trim($NF);
   if ($2 ~ /Alloc/) { id=++nid; live[ad]=id; qid[tid,n]=id; owner[id]=strtonum(trim($10)) } else { if (ad in live) { freedid[live[ad]]=1; delete live[ad] } qid[tid,n]=0 }
   next }
/^ +StackWalk/ { tid=strtonum(trim($22)); ts=trim($17)+0; h=qh[tid]+1;
   while (h < qt[tid] && qts[tid,h+1] <= ts) h++;
   if (h<=qt[tid] && qts[tid,h] <= ts && ts-qts[tid,h] < 5000000) { qh[tid]=h; id=qid[tid,h]; if (id>0) { if (index($0,"0xFFFFF8017BEAD9B1")>0 || index($0,"0xFFFFF8017BEAD464")>0) iscp[id]=1 } }
   next }
END{ for (id in iscp) { p=owner[id]; tot[p]++; if (!(id in freedid)) lk[p]++ }
  for (p in tot) printf "%6d %-24s createproc-allocs=%5d leaked=%5d (%.0f%%)\n", p, name[p], tot[p], lk[p], 100*lk[p]/tot[p] }
