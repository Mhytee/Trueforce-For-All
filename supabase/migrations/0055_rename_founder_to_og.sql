-- Rename the 'founder' achievement label to "OG": "Founder" read too close to the creator/Tuner
-- achievements and collided with "Founding Supporter". Key + metric stay 'founder' (internal).
update public.achievements
   set label = 'OG',
       description = 'One of the original Trueforce For All users'
 where key = 'founder';
