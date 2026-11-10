function trim(s){gsub(/^[ \t]+|[ \t]+$/,"",s);return s}
BEGIN{FS=","}
/^ +Process, +Start/ { pp=strtonum(trim($22)); if (pp==TARGET) { img="?"; if (match($0,/"[^"]+\.[Ee][Xx][Ee]"/)) img=substr($0,RSTART+1,RLENGTH-2); ps[++nps]=img } next }
/^ +Pool, +(PoolAllocation|PoolFree)/ { tid=strtonum(trim($11)); n=++qt[tid]; qts[tid,n]=trim($17)+0; ad=trim($NF);
   if ($2 ~ /Alloc/) { id=++nid; live[ad]=id; qid[tid,n]=id; own[id]=strtonum(trim($10)); at[id]=trim($17)+0 } else { if (ad in live) { fid=live[ad]; ft[fid]=trim($17)+0; delete live[ad] } qid[tid,n]=0 }
   next }
/^ +StackWalk/ { tid=strtonum(trim($22)); ts=trim($17)+0; h=qh[tid]+1;
   while (h < qt[tid] && qts[tid,h+1] <= ts) h++;
   if (h<=qt[tid] && qts[tid,h] <= ts && ts-qts[tid,h] < 5000000) { qh[tid]=h; id=qid[tid,h]; if (id>0 && (index($0,"0xFFFFF8017BEAD9B1")>0 || index($0,"0xFFFFF8017BEAD464")>0) && own[id]==TARGET) cp[id]=1 }
   next }
END{ # order CP allocs by id
  k=0; for (id=1; id<=nid; id++) if (id in cp) { k++; img=ps[k]; cnt[img]++; if (id in ft) { fr[img]++; life=(ft[id]-at[id])/1e7; sum[img]+=life; if (life>mx[img]) mx[img]=life } }
  for (i in cnt) printf "%-14s cp-allocs=%d freed=%d avgLife=%.2fs maxLife=%.2fs\n", i, cnt[i], fr[i], (fr[i]?sum[i]/fr[i]:0), mx[i] }
