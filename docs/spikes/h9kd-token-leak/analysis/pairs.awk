function trim(s){gsub(/^[ \t]+|[ \t]+$/,"",s);return s}
BEGIN{FS=","}
/^ +Process, +(Start|DCStart)/ { cp_=strtonum(trim($21)); img="?"; if (match($0,/"[^"]+\.[Ee][Xx][Ee]"/)) img=substr($0,RSTART+1,RLENGTH-2); nm[cp_]=img; if ($2 ~ /Start/ && $2 !~ /DC/) { pp=strtonum(trim($22)); np[pp]++; ps[pp,np[pp]]=img } next }
/^ +Pool, +(PoolAllocation|PoolFree)/ { tid=strtonum(trim($11)); n=++qt[tid]; qts[tid,n]=trim($17)+0; ad=trim($NF);
   if ($2 ~ /Alloc/) { id=++nid; live[ad]=id; qid[tid,n]=id; own[id]=strtonum(trim($10)); at[id]=trim($17)+0 } else { if (ad in live) { freedid[live[ad]]=1; ft[live[ad]]=trim($17)+0; delete live[ad] } qid[tid,n]=0 }
   next }
/^ +StackWalk/ { tid=strtonum(trim($22)); ts=trim($17)+0; h=qh[tid]+1;
   while (h < qt[tid] && qts[tid,h+1] <= ts) h++;
   if (h<=qt[tid] && qts[tid,h] <= ts && ts-qts[tid,h] < 5000000) { qh[tid]=h; id=qid[tid,h]; if (id>0 && (index($0,"0xFFFFF8017BEAD9B1")>0 || index($0,"0xFFFFF8017BEAD464")>0)) cp[id]=1 }
   next }
END{ for (id=1; id<=nid; id++) if (id in cp) { p=own[id]; k=++kc[p]; child=(k<=np[p])?ps[p,k]:"?"; key=nm[p] ">" child; tot[key]++; if (!(id in freedid)) lk[key]++; else { s2[key]+=(ft[id]-at[id])/1e7 } }
  for (k in tot) printf "%-40s n=%4d leaked=%4d\n", k, tot[k], lk[k] }
